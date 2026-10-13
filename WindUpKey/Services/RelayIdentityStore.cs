using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using WindUpKey.Protocol;

namespace WindUpKey.Services;

/// <summary>What importing a pasted relay identity would do (or did).</summary>
public enum RelayIdentityImport
{
    /// <summary>Valid and different from the current identity: it replaces it (the old one is kept for revert).</summary>
    Replace,

    /// <summary>Same as the current identity: nothing to do.</summary>
    Unchanged,

    /// <summary>Not a valid relay identity.</summary>
    Invalid,

    /// <summary>No character is logged in.</summary>
    NoCharacter,

    /// <summary>The identity file couldn't be read or written; nothing changed.</summary>
    StoreUnavailable,

    /// <summary>
    /// The identity file, its lock file or its folder can't be written (read-only attribute, permissions); nothing
    /// changed. Existing identities still work.
    /// </summary>
    NotWritable,

    /// <summary>The identity file was written by a newer build, so this build never changes it.</summary>
    ReadOnly,

    /// <summary>
    /// Already the saved identity (another client may have saved it), but the relay had rejected this session or it
    /// registered with a different value: nothing changed on disk, and the client reconnected with it. Only
    /// <see cref="RelayClient"/> returns this.
    /// </summary>
    Reconnected,
}

/// <summary>Whether the relay identity file can be used right now.</summary>
public enum RelayIdentityStoreStatus
{
    /// <summary>Read (or not created yet) and writable.</summary>
    Available,

    /// <summary>Written by a newer build: its entries can be used, but this build never changes the file.</summary>
    ReadOnly,

    /// <summary>The last read or write failed (file busy or held by another client, or unreadable); retried later.</summary>
    Unavailable,

    /// <summary>
    /// Readable, but the file, its <c>.lock</c> file or its folder can't be written (a read-only attribute, for
    /// example after copying pluginConfigs from another PC, or permissions): existing secrets work, nothing can be
    /// created or changed. Checked again on every read and write.
    /// </summary>
    NotWritable,
}

#if WINDUP_TESTING || WINDUP_SELFTEST_UNITS
/// <summary>Which version the identity file is at, as the store last read it (04-D4 self-test).</summary>
public enum RelayIdentityFileVersion
{
    Missing,
    Current,

    /// <summary>Written by a newer build: read-only (01-D19).</summary>
    Newer,
}

/// <summary>Whether the character's entry records its pairing key (01-D30), and whether it matches.</summary>
public enum RelayIdentityKeyRecord
{
    /// <summary>Not recorded yet (an older entry; written on the next write for this character).</summary>
    Absent,
    Matches,
    Differs,
}

/// <summary>
/// What the store knows about one character's identity, without any value (04-D4 self-test): the file's name (it sits
/// in pluginConfigs), whether it is outside the plugin's own config folder (as Dalamud names it, 04-D10), its version
/// and status, the entry's validity, the recorded pairing key's match, and how many <c>.corrupt-*</c> copies there are.
/// </summary>
public sealed record RelayIdentityDiagnosis(
    string FileName,
    bool OutsidePluginFolder,
    bool FileExists,
    RelayIdentityFileVersion Version,
    RelayIdentityStoreStatus Status,
    bool HasEntry,
    bool SecretValid,
    bool PreviousValid,
    RelayIdentityKeyRecord PairingKey,
    int BackupCount);
#endif

/// <summary>
/// Per-character relay secrets (current and previous), keyed by ContentId hex, in
/// <c>pluginConfigs/&lt;InternalName&gt;.relay-identity.json</c>: next to the plugin config folder, not inside it,
/// so resetting or uninstalling the plugin's data, a config reset, or a build that doesn't know about them
/// (other channel, downgrade) can't lose them.
/// <para>
/// No Dalamud dependency: the folder and name are injected, so this can be tested directly. One instance is
/// not thread-safe — call it from the framework thread only (connect preparation and the config UI). Never log
/// a secret.
/// </para>
/// <para>
/// Several game clients may share the file. Every write takes an exclusive cross-process lock (an OS handle on
/// a <c>.lock</c> file opened with <see cref="FileShare.None"/>, so a lock file left by a crash never wedges
/// it), re-reads the file, merges in only the one character being changed, replaces the file atomically (temp
/// file, then move) and releases the lock. Disk reads take the same lock. When the lock file can't be opened for
/// writing at all (read-only attribute or permissions, not another client), its read-only attribute is cleared and
/// it is tried once more; if that fails too, the file is read without the lock (safe, since every write replaces
/// it atomically) and nothing is written: existing secrets keep working and only changes are refused
/// (<see cref="RelayIdentityStoreStatus.NotWritable"/>). A save refused for access gets the same treatment.
/// </para>
/// <para>
/// A file that fails to parse at a version this build knows is never overwritten: it is moved aside as
/// <c>….corrupt-&lt;utc&gt;</c>, a warning without values is logged, and the store carries on with the secrets this
/// client already knows (or empty). When only some values fail validation, the original is copied aside the same
/// way (once per distinct content) and the file is rewritten without them; if it can't be copied or rewritten,
/// the valid entries are still used and only changes are refused. A file from a newer build is read-only: its
/// usable entries are used, it is never moved or written. Read and lock errors only make the store unavailable
/// for a while. Unknown JSON fields survive a rewrite.
/// </para>
/// <para>
/// A secret this client has seen (read or written) is remembered for the session: when the file on disk lacks a
/// valid value for it, the remembered one is written back instead of generating a new one or dropping it. Until it
/// is actually back on disk, it is never handed out for a register.
/// </para>
/// <para>
/// Each entry this build writes also records the character's pairing key (<c>pairingKey</c>), so a kept-aside copy
/// can be matched to the key shown in the UI. It is informational only: never used to authenticate, never sent.
/// </para>
/// </summary>
public sealed class RelayIdentityStore
{
    public const string FileSuffix = ".relay-identity.json";
    public const int CurrentVersion = 1;

    /// <summary>
    /// How long a read or write waits for another client's lock. Short, because it blocks the framework thread;
    /// a timeout only makes the store unavailable until the next retry (the next connect attempt, or the UI's retry).
    /// </summary>
    public static readonly TimeSpan DefaultLockWait = TimeSpan.FromMilliseconds(150);

    private const string CorruptSuffix = ".corrupt-";
    private const string SaveState = "save";
    private const string SaveAccessState = "save-access";
    private const string StampFormat = "yyyyMMdd'T'HHmmss'Z'";
    private const int StampLength = 16; // yyyyMMddTHHmmssZ

    private static readonly TimeSpan LoadRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LockRetryInterval = TimeSpan.FromMilliseconds(15);
    private static readonly IReadOnlySet<string> EmptyIds = new HashSet<string>(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly string _lockPath;
    private readonly string _tempPath;
    private readonly string _fileName;
    private readonly Action<string> _warn;
    private readonly Func<DateTime> _utcNow;
    private readonly TimeSpan _lockWait;

    /// <summary>The last good view of the file. Null until loaded, and after a load that failed.</summary>
    private Dictionary<string, Entry>? _characters;

    /// <summary>True when the file on disk (as last read) is from a newer build.</summary>
    private bool _readOnly;

    /// <summary>True when the last disk read or write failed for a reason that should clear by itself.</summary>
    private bool _unavailable;

    /// <summary>
    /// True when the last read had to go without the lock (the lock file can't be opened for writing), or the last
    /// save was refused for access: nothing can be written until that changes.
    /// </summary>
    private bool _notWritable;

    /// <summary>True when the file on disk (as last read) is partly invalid and couldn't be copied aside, so it is never written.</summary>
    private bool _salvaged;

    /// <summary>
    /// Characters whose secret in the current view was restored from memory but couldn't be written back yet: it
    /// isn't on disk, so it is never handed out for a register.
    /// </summary>
    private IReadOnlySet<string> _notOnDisk = EmptyIds;

    /// <summary>After a failed load, the next attempt waits until then (the UI asks every frame).</summary>
    private DateTime _nextLoadAttemptUtc = DateTime.MinValue;

    /// <summary>
    /// The unavailable/failure states already logged. Cleared by a successful read, except save failures, which only
    /// a successful save clears (reads keep working while saves fail).
    /// </summary>
    private readonly HashSet<string> _warnedStates = new(StringComparer.Ordinal);

    /// <summary>
    /// Every character's last valid entry this client has seen on disk or saved this session, kept when the file
    /// later lacks it, so it is restored rather than regenerated or lost.
    /// </summary>
    private readonly Dictionary<string, Entry> _remembered = new(StringComparer.Ordinal);

    /// <summary>The content of the partly invalid file last kept aside, so the same content is copied only once.</summary>
    private string? _keptAsideContent;

    /// <param name="pluginConfigDirectory">The plugin's own config folder (<c>GetPluginConfigDirectory()</c>).</param>
    /// <param name="internalName">The plugin's internal name; the file is named after it, next to that folder.</param>
    /// <param name="lockWait">How long to wait for another client's lock; <see cref="DefaultLockWait"/> when null.</param>
    public RelayIdentityStore(
        string pluginConfigDirectory,
        string internalName,
        Action<string> warn,
        Func<DateTime>? utcNow = null,
        TimeSpan? lockWait = null)
    {
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pluginConfigDirectory));
        var parent = Path.GetDirectoryName(folder) ?? folder;
        _fileName = internalName + FileSuffix;
        _path = Path.Combine(parent, _fileName);
        _lockPath = _path + ".lock";
        _tempPath = _path + ".tmp";
        _warn = warn;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _lockWait = lockWait ?? DefaultLockWait;
    }

    /// <summary>Whether the file can be used right now. Reflects the last read or write.</summary>
    public RelayIdentityStoreStatus Status =>
        _characters is null || _unavailable ? RelayIdentityStoreStatus.Unavailable
        : _readOnly ? RelayIdentityStoreStatus.ReadOnly
        : _notWritable ? RelayIdentityStoreStatus.NotWritable
        : RelayIdentityStoreStatus.Available;

    /// <summary>The character's current secret, or empty when it has none (or the store is unavailable).</summary>
    public string GetSecret(string contentId) =>
        TryGetEntry(contentId) is { } entry && RelaySecret.IsValid(entry.Secret) ? entry.Secret! : string.Empty;

    /// <summary>
    /// True when the character has a previous secret to revert to and the file can be changed: not from a newer build,
    /// not <see cref="RelayIdentityStoreStatus.NotWritable"/>, and not a partly invalid file that couldn't be copied aside.
    /// </summary>
    public bool CanRevert(string contentId) =>
        TryGetEntry(contentId) is { } entry && !_readOnly && !_notWritable && !_salvaged && RelaySecret.IsValid(entry.Previous);

    /// <summary>The next read retries now instead of after the retry delay (login, Reconnect).</summary>
    public void ResetRetryDelay() => _nextLoadAttemptUtc = DateTime.MinValue;

    /// <summary>
    /// Returns the character's secret as it is on disk now, generating and saving one the first time. Always
    /// re-reads the file under the lock (one small read per connect), since another client may have created or
    /// imported it. Returns null when the store can't be read, is read-only or not writable without an entry for the
    /// character, or the new secret can't be saved: a secret that isn't on disk must never be sent, or the relay
    /// would bind the key to a value lost on restart. When the file lacks a valid secret this client already knows
    /// (another client rewrote it without the entry, or it was damaged), that secret is written back instead of a
    /// new one, and handed out only once it is back on disk (until then the store is unavailable).
    /// </summary>
    public string? EnsureSecret(string contentId, out bool generated)
    {
        generated = false;
        if (string.IsNullOrEmpty(contentId))
            return null;

        var key = NormalizeId(contentId);
        string? saved = null;
        var created = false;
        var outcome = Update(key, existing =>
        {
            if (RelaySecret.IsValid(existing?.Secret))
                return null;
            if (Restore(existing, _remembered.GetValueOrDefault(key)) is { } restored)
            {
                saved = restored.Secret;
                return restored;
            }

            saved = RelaySecret.Generate();
            created = true;
            return new Entry { Secret = saved, Previous = existing?.Previous, Extra = existing?.Extra };
        });

        switch (outcome)
        {
            case UpdateOutcome.Saved:
                generated = created;
                return saved;
            case UpdateOutcome.NoChange or UpdateOutcome.ReadOnly:
                // A secret restored from memory that couldn't be written back (the file couldn't be copied aside or
                // rewritten) isn't on disk: wait, as for an unreadable file, until it is.
                if (_notOnDisk.Contains(key))
                {
                    _unavailable = true;
                    return null;
                }

                // The view was just replaced with what is on disk; read-only files are never added to.
                return _characters is not null && _characters.TryGetValue(key, out var found) && RelaySecret.IsValid(found.Secret)
                    ? found.Secret
                    : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// What <see cref="Import"/> would do with this pasted value, without changing anything. Compares against the
    /// file as it is on disk now (re-read under the lock), not this client's last view of it.
    /// </summary>
    public RelayIdentityImport CheckImport(string contentId, string? raw)
    {
        if (string.IsNullOrEmpty(contentId))
            return RelayIdentityImport.NoCharacter;

        var secret = RelaySecret.Normalize(raw);
        if (!RelaySecret.IsValid(secret))
            return RelayIdentityImport.Invalid;

        if (Refresh() is not { } characters)
            return RelayIdentityImport.StoreUnavailable;

        if (characters.TryGetValue(NormalizeId(contentId), out var entry)
            && string.Equals(entry.Secret, secret, StringComparison.Ordinal))
            return RelayIdentityImport.Unchanged;

        // Said before the user confirms, since the import would fail anyway.
        if (_readOnly)
            return RelayIdentityImport.ReadOnly;
        if (_notWritable)
            return RelayIdentityImport.NotWritable;
        return _salvaged ? RelayIdentityImport.StoreUnavailable : RelayIdentityImport.Replace;
    }

    /// <summary>
    /// Replaces the character's secret with one copied from another install, keeping the old one as the
    /// previous secret. Importing the current secret changes nothing.
    /// </summary>
    public RelayIdentityImport Import(string contentId, string? raw)
    {
        var result = CheckImport(contentId, raw);
        if (result != RelayIdentityImport.Replace)
            return result;

        var secret = RelaySecret.Normalize(raw);
        var key = NormalizeId(contentId);
        var outcome = Update(key, existing =>
        {
            if (string.Equals(existing?.Secret, secret, StringComparison.Ordinal))
                return null;

            // The file lacks a valid secret this client knows (decision 26): that one is what is being replaced, so
            // it becomes the previous one instead of being lost (decision 11).
            var current = RelaySecret.IsValid(existing?.Secret)
                ? existing
                : Restore(existing, _remembered.GetValueOrDefault(key)) ?? existing;
            var previous = RelaySecret.IsValid(current?.Secret) && !string.Equals(current!.Secret, secret, StringComparison.Ordinal)
                ? current.Secret
                : current?.Previous;
            return new Entry { Secret = secret, Previous = previous, Extra = current?.Extra };
        });

        return outcome switch
        {
            UpdateOutcome.Saved => RelayIdentityImport.Replace,
            UpdateOutcome.NoChange => RelayIdentityImport.Unchanged,
            UpdateOutcome.ReadOnly => RelayIdentityImport.ReadOnly,
            UpdateOutcome.NotWritable => RelayIdentityImport.NotWritable,
            _ => RelayIdentityImport.StoreUnavailable,
        };
    }

    /// <summary>Swaps the character's current and previous secrets. False when there is no previous one or saving failed.</summary>
    public bool Revert(string contentId)
    {
        if (string.IsNullOrEmpty(contentId) || Load() is null)
            return false;

        var outcome = Update(NormalizeId(contentId), existing =>
            existing is not null && RelaySecret.IsValid(existing.Previous)
                ? new Entry { Secret = existing.Previous, Previous = existing.Secret, Extra = existing.Extra }
                : null);
        return outcome == UpdateOutcome.Saved;
    }

#if WINDUP_TESTING || WINDUP_SELFTEST_UNITS
    /// <summary>
    /// 04-D4 self-test: what the store knows about this character's identity, without any value (no secret, hash,
    /// ContentId or key). Uses the current view and never reads under the lock or writes, so it can't move a damaged file
    /// aside or rewrite one. <paramref name="expectedPairingKey"/> is the key derived from the ContentId; only whether
    /// the entry's recorded key matches it is returned. <paramref name="pluginConfigDirectory"/> is the folder Dalamud's
    /// reset deletes, asked of Dalamud by the caller rather than taken from this store's own path (04-D10): the file must
    /// not be in it or under it.
    /// </summary>
    public RelayIdentityDiagnosis Diagnose(string contentId, string expectedPairingKey, string pluginConfigDirectory)
    {
        var entry = ViewEntry(contentId);
        var outside = IsOutside(Path.GetDirectoryName(_path) ?? _path, pluginConfigDirectory);
        bool exists;
        try
        {
            exists = File.Exists(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            exists = false;
        }

        var keyRecord = string.IsNullOrEmpty(entry?.PairingKey) ? RelayIdentityKeyRecord.Absent
            : string.Equals(entry.PairingKey, expectedPairingKey, StringComparison.Ordinal) ? RelayIdentityKeyRecord.Matches
            : RelayIdentityKeyRecord.Differs;
        return new RelayIdentityDiagnosis(
            _fileName,
            outside,
            exists,
            !exists ? RelayIdentityFileVersion.Missing : _readOnly ? RelayIdentityFileVersion.Newer : RelayIdentityFileVersion.Current,
            Status,
            entry is not null,
            RelaySecret.IsValid(entry?.Secret),
            RelaySecret.IsValid(entry?.Previous),
            keyRecord,
            BackupFiles().Count);
    }

    /// <summary>
    /// 04-D5 self-test: whether <paramref name="candidate"/> is this character's current or previous secret, so a probe's
    /// fresh random secret can be checked against both without the secrets leaving the store. Current view only.
    /// </summary>
    public bool IsKnownSecret(string contentId, string candidate) =>
        ViewEntry(contentId) is { } entry
        && (string.Equals(entry.Secret, candidate, StringComparison.Ordinal)
            || string.Equals(entry.Previous, candidate, StringComparison.Ordinal));

    /// <summary>
    /// 04-D3 self-test: the values the report's final scrubber must replace for this character: its current and previous
    /// secret and their hashes. For the scrubber only; never log them.
    /// </summary>
    public IReadOnlyList<string> SensitiveValues(string contentId)
    {
        var values = new List<string>();
        if (ViewEntry(contentId) is not { } entry)
            return values;
        foreach (var secret in new[] { entry.Secret, entry.Previous })
        {
            if (string.IsNullOrEmpty(secret))
                continue;
            values.Add(secret);
            if (RelaySecret.HashHex(secret) is { } hash)
                values.Add(hash);
        }

        return values;
    }

    /// <summary>True when <paramref name="directory"/> is neither <paramref name="folder"/> nor inside it.</summary>
    private static bool IsOutside(string directory, string folder)
    {
        string dir, root;
        try
        {
            dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }

        return !string.Equals(dir, root, StringComparison.OrdinalIgnoreCase)
               && !dir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The character's entry in the current view, without loading or refreshing it (self-test reads only).</summary>
    private Entry? ViewEntry(string contentId) =>
        !string.IsNullOrEmpty(contentId) && _characters is not null && _characters.TryGetValue(NormalizeId(contentId), out var entry)
            ? entry
            : null;
#endif

    private Entry? TryGetEntry(string contentId)
    {
        if (string.IsNullOrEmpty(contentId))
            return null;
        var characters = Load();
        return characters is not null && characters.TryGetValue(NormalizeId(contentId), out var entry) ? entry : null;
    }

    private static string NormalizeId(string contentId) => contentId.Trim().ToUpperInvariant();

    // ---------- reading ----------

    private Dictionary<string, Entry>? Load()
    {
        if (_characters is not null)
            return _characters;

        if (_utcNow() < _nextLoadAttemptUtc)
            return null;

        return Refresh();
    }

    /// <summary>
    /// Re-reads the file under the lock (or without it, when the lock file can't be opened for writing) and makes it
    /// the current view. Null (view kept) when it can't be read.
    /// </summary>
    private Dictionary<string, Entry>? Refresh()
    {
        using (var held = AcquireLock(out var lockNotWritable))
        {
            var disk = held is not null ? ReadRecovered()
                : lockNotWritable ? ReadWithoutLock()
                : DiskState.Unavailable;
            if (disk.Status is DiskStatus.Ok or DiskStatus.Missing or DiskStatus.Newer or DiskStatus.Salvaged)
            {
                Accept(disk);
                return _characters;
            }
        }

        _nextLoadAttemptUtc = _utcNow() + LoadRetryDelay;
        return null;
    }

    /// <summary>
    /// Reads the file and deals with a bad one: a file that doesn't parse is moved aside (and the secrets this
    /// client knows are written back, if any); a file with some invalid values is copied aside (once per distinct
    /// content) and rewritten without them, with the values this client knows put back. Call with the lock held.
    /// Returns Ok, Missing, Newer, Salvaged or Unavailable.
    /// </summary>
    private DiskState ReadRecovered()
    {
        var disk = ReadDisk();
        switch (disk.Status)
        {
            case DiskStatus.Corrupt:
            {
                if (!Quarantine(keepOriginal: false))
                    return DiskState.Unavailable;

                // Carry on with the secrets this client knows, rather than nothing.
                var carried = NewFile();
                foreach (var (id, entry) in _remembered)
                    carried.Characters![id] = entry;
                if (carried.Characters!.Count == 0)
                    return DiskState.Empty;
                return TryWrite(carried) ? new DiskState(DiskStatus.Ok, carried, null) : DiskState.Unavailable;
            }

            case DiskStatus.Partial:
            {
                // Only the invalid values are dropped. Ones this client knows are put back.
                var file = disk.File!;
                var restoredSecrets = new HashSet<string>(StringComparer.Ordinal);
                foreach (var id in disk.Damaged!)
                {
                    file.Characters!.TryGetValue(id, out var onDisk);
                    if (Restore(onDisk, _remembered.GetValueOrDefault(id)) is { } restored)
                    {
                        file.Characters[id] = restored;
                        if (!RelaySecret.IsValid(onDisk?.Secret))
                            restoredSecrets.Add(id);
                    }
                }

                // Never rewritten without a copy of the original. Either way the valid entries are used, so
                // existing secrets keep working while the file is held (read-only attribute, antivirus, sync);
                // restored secrets only count once they are on disk.
                var salvaged = disk with { Damaged = null, Text = null };
                if (!KeepAsideOnce(disk.Text!))
                    return salvaged with { Status = DiskStatus.Salvaged, NotOnDisk = restoredSecrets };
                return TryWrite(file)
                    ? salvaged with { Status = DiskStatus.Ok }
                    : salvaged with { Status = DiskStatus.Ok, Unsaved = true, NotOnDisk = restoredSecrets };
            }

            default:
                return disk;
        }
    }

    /// <summary>
    /// What to write back for a character whose entry on disk lacks a valid value this client knows (decisions 26
    /// and 27): the known secret, with the previous one, when the disk has no valid secret; the known previous
    /// when the disk has the same secret without one. Null when there is nothing to restore.
    /// </summary>
    private static Entry? Restore(Entry? onDisk, Entry? known)
    {
        if (known is null || !RelaySecret.IsValid(known.Secret))
            return null;

        if (!RelaySecret.IsValid(onDisk?.Secret))
        {
            var previous = RelaySecret.IsValid(onDisk?.Previous) ? onDisk!.Previous : known.Previous;
            return new Entry
            {
                Secret = known.Secret,
                Previous = string.Equals(previous, known.Secret, StringComparison.Ordinal) ? null : previous,
                PairingKey = onDisk?.PairingKey ?? known.PairingKey,
                Extra = onDisk?.Extra ?? known.Extra,
            };
        }

        if (!RelaySecret.IsValid(onDisk!.Previous)
            && RelaySecret.IsValid(known.Previous)
            && string.Equals(onDisk.Secret, known.Secret, StringComparison.Ordinal))
            return new Entry { Secret = onDisk.Secret, Previous = known.Previous, PairingKey = onDisk.PairingKey, Extra = onDisk.Extra };

        return null;
    }

    private static IdentityFile NewFile() =>
        new() { Characters = new SortedDictionary<string, Entry?>(StringComparer.Ordinal) };

    /// <summary>
    /// Takes a disk view as the current one, and says once if it is from a newer build. A view that couldn't be
    /// saved back (see <see cref="DiskState.Unsaved"/>), or that was read without the lock, keeps its failure logged
    /// once, not once per retry.
    /// </summary>
    private void Accept(DiskState disk)
    {
        SetView(disk.Characters(), disk.NotOnDisk);
        _readOnly = disk.Status == DiskStatus.Newer;
        _salvaged = disk.Status == DiskStatus.Salvaged;
        _notWritable = disk.Unlocked;
        _unavailable = false;
        if (_readOnly)
            WarnOnce("newer", $"WindUpKey: {_fileName} was written by a newer Wind-Up Key build; using it read-only");
        else if (!disk.Unsaved && !_salvaged && !disk.Unlocked)
            _warnedStates.RemoveWhere(state => state is not (SaveState or SaveAccessState));
    }

    /// <summary>
    /// Makes <paramref name="characters"/> the current view, and remembers its valid secrets.
    /// <paramref name="notOnDisk"/>: the characters whose secret in it isn't on disk (restored, not written back yet).
    /// </summary>
    private void SetView(Dictionary<string, Entry> characters, IReadOnlySet<string>? notOnDisk)
    {
        _characters = characters;
        _notOnDisk = notOnDisk ?? EmptyIds;
        foreach (var (id, entry) in characters)
        {
            if (RelaySecret.IsValid(entry.Secret))
                _remembered[id] = entry;
        }
    }

    /// <summary>
    /// Reads the file without the lock, because the lock file can't be opened for writing (see
    /// <see cref="AcquireLock"/>). Safe, since every write replaces the file atomically: this sees the old file or the
    /// new one, never a mix. Never moves or writes anything, and the result is marked
    /// <see cref="DiskState.Unlocked"/> so nothing is written from it: a partly invalid file is used for its valid
    /// values, and one that doesn't parse is left where it is (and nothing is used from it) until it can be moved aside.
    /// </summary>
    private DiskState ReadWithoutLock()
    {
        var disk = ReadDisk();
        switch (disk.Status)
        {
            case DiskStatus.Unavailable:
                return disk;
            case DiskStatus.Partial:
                return disk with { Status = DiskStatus.Salvaged, Damaged = null, Text = null, Unlocked = true };
            case DiskStatus.Corrupt:
                WarnOnce("corrupt-unlocked", $"WindUpKey: {_fileName} could not be read, and it can't be moved aside while its lock file can't be written; relay identity unavailable until it can");
                return DiskState.Empty with { Unlocked = true };
            default:
                return disk with { Unlocked = true };
        }
    }

    /// <summary>Reads and classifies the file. Call with the lock held.</summary>
    private DiskState ReadDisk()
    {
        string text;
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            text = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return DiskState.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Antivirus, cloud sync or a backup tool holding the file: not corruption. Retry later.
            MarkUnavailable("read", $"WindUpKey: could not read {_fileName} ({ex.GetType().Name}); relay identity unavailable for now, retrying");
            return DiskState.Unavailable;
        }

        return Parse(text);
    }

    private static DiskState Parse(string text)
    {
        int version;
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var versionElement))
                return DiskState.Corrupt;

            // A version this build can't read as an integer (a string, a fraction, out of range) can only come from
            // a newer build, so it counts as newer: read-only, never quarantined.
            if (versionElement.ValueKind != JsonValueKind.Number
                || !versionElement.TryGetInt32(out version)
                || version > CurrentVersion)
                return new DiskState(DiskStatus.Newer, null, ReadNewerEntries(root));
        }
        catch (JsonException)
        {
            // Not JSON at all. The exception text could quote file contents, so it isn't logged.
            return DiskState.Corrupt;
        }

        if (version != CurrentVersion)
            return DiskState.Corrupt;

        StoredFile? stored;
        try
        {
            // Entries stay raw here, so one bad entry doesn't fail the whole file.
            stored = JsonSerializer.Deserialize<StoredFile>(text, JsonOptions);
        }
        catch (JsonException)
        {
            return DiskState.Corrupt;
        }

        if (stored is null)
            return DiskState.Corrupt;
        var validated = Validate(stored);
        return validated.Status == DiskStatus.Partial ? validated with { Text = text } : validated;
    }

    /// <summary>From a newer file, takes only the entries this build understands; skips everything else.</summary>
    private static Dictionary<string, Entry> ReadNewerEntries(JsonElement root)
    {
        var characters = new Dictionary<string, Entry>(StringComparer.Ordinal);
        if (!root.TryGetProperty("characters", out var map) || map.ValueKind != JsonValueKind.Object)
            return characters;

        foreach (var property in map.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name) || property.Value.ValueKind != JsonValueKind.Object)
                continue;
            var secret = ReadString(property.Value, "secret");
            if (!RelaySecret.IsValid(secret))
                continue;
            var previous = ReadString(property.Value, "previous");
            characters.TryAdd(NormalizeId(property.Name), new Entry
            {
                Secret = secret,
                Previous = RelaySecret.IsValid(previous) ? previous : null,
            });
        }

        return characters;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// Keeps the valid values of a current-version file, keyed by normalized ContentId: Ok when nothing was dropped,
    /// otherwise Partial. Field by field: an entry keeps whichever of its secret and previous is a canonical secret,
    /// and is dropped only when neither is. Two entries for the same character are both dropped, since neither can
    /// be trusted. Unknown fields are kept.
    /// </summary>
    private static DiskState Validate(StoredFile stored)
    {
        var file = NewFile();
        file.Version = stored.Version;
        file.Extra = stored.Extra;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var damaged = new HashSet<string>(StringComparer.Ordinal);
        var anyDropped = false;
        foreach (var (id, element) in stored.Characters ?? new Dictionary<string, JsonElement>())
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                anyDropped = true;
                continue;
            }

            var key = NormalizeId(id);
            if (!seen.Add(key))
            {
                file.Characters!.Remove(key);
                damaged.Add(key);
                anyDropped = true;
                continue;
            }

            var entry = TryReadEntry(element, out var entryDamaged);
            if (entryDamaged)
            {
                damaged.Add(key);
                anyDropped = true;
            }

            if (entry is not null)
                file.Characters![key] = entry;
        }

        return anyDropped
            ? new DiskState(DiskStatus.Partial, file, null) { Damaged = damaged }
            : new DiskState(DiskStatus.Ok, file, null);
    }

    /// <summary>
    /// One stored entry with its valid values, or null when it isn't an object or has neither a valid secret nor a
    /// valid previous. <paramref name="damaged"/> is true when anything in it was dropped: a value that isn't a
    /// canonical secret (or isn't a string), a repeated value, or the whole entry.
    /// </summary>
    private static Entry? TryReadEntry(JsonElement element, out bool damaged)
    {
        damaged = false;
        if (element.ValueKind != JsonValueKind.Object)
        {
            damaged = true;
            return null;
        }

        var entry = new Entry();
        bool secretSeen = false, previousSeen = false, secretBad = false, previousBad = false;
        foreach (var property in element.EnumerateObject())
        {
            switch (property.Name)
            {
                case "secret":
                    // A repeated value is dropped too: neither copy can be trusted.
                    secretBad |= !TryReadValue(property.Value, out var secret) || secretSeen;
                    entry.Secret = secret;
                    secretSeen = true;
                    break;
                case "previous":
                    previousBad |= !TryReadValue(property.Value, out var previous) || previousSeen;
                    entry.Previous = previous;
                    previousSeen = true;
                    break;
                case "pairingKey":
                    // Informational only (decision 30): a value that isn't a string is dropped without counting as
                    // damage, and the next write for this character records it again.
                    entry.PairingKey = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                    break;
                default:
                    (entry.Extra ??= new Dictionary<string, JsonElement>())[property.Name] = property.Value.Clone();
                    break;
            }
        }

        if (secretBad)
            entry.Secret = null;
        if (previousBad)
            entry.Previous = null;
        damaged = secretBad || previousBad;
        if (damaged && entry.Secret is null && entry.Previous is null)
            return null;
        return entry;
    }

    /// <summary>A stored value: absent (null or empty) or a canonical secret. False when it is anything else.</summary>
    private static bool TryReadValue(JsonElement value, out string? secret)
    {
        secret = null;
        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
                return true;
            case JsonValueKind.String:
                var text = value.GetString();
                if (string.IsNullOrEmpty(text))
                    return true;
                if (!RelaySecret.IsValid(text))
                    return false;
                secret = text;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Makes sure a copy of the partly invalid file (content <paramref name="text"/>) is kept aside, copying it only
    /// when no copy with that content exists yet, so a file that can't be rewritten isn't copied again on every
    /// retry. False when it couldn't be copied. Call with the lock held.
    /// </summary>
    private bool KeepAsideOnce(string text)
    {
        if (string.Equals(_keptAsideContent, text, StringComparison.Ordinal) || HasCopyWithContent(text)
            || Quarantine(keepOriginal: true))
        {
            _keptAsideContent = text;
            return true;
        }

        return false;
    }

    /// <summary>True when one of the kept-aside copies (from this client, another one or an earlier session) has this content.</summary>
    private bool HasCopyWithContent(string text)
    {
        foreach (var path in BackupFiles())
        {
            try
            {
                if (string.Equals(File.ReadAllText(path, Encoding.UTF8), text, StringComparison.Ordinal))
                    return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Can't tell: not a match, so at worst one more copy is kept.
            }
        }

        return false;
    }

    /// <summary>
    /// The file name (not the path) of the newest kept-aside copy of the identity file
    /// (<c>….corrupt-&lt;utc&gt;</c>), or null when there is none. Never reads the copies. For the rejection
    /// message: an older copy may hold the secret to import.
    /// </summary>
    public string? NewestBackupFileName()
    {
        string? newest = null;
        (string Stamp, int N) newestOrder = default;
        foreach (var path in BackupFiles())
        {
            var name = Path.GetFileName(path);
            var order = BackupOrder(name);
            var compared = newest is null ? 1 : string.CompareOrdinal(order.Stamp, newestOrder.Stamp);
            if (compared == 0)
                compared = order.N != newestOrder.N ? order.N.CompareTo(newestOrder.N) : string.CompareOrdinal(name, newest);
            if (compared > 0)
            {
                newest = name;
                newestOrder = order;
            }
        }

        return newest;
    }

    /// <summary>The kept-aside copies next to the file; empty when the folder can't be listed.</summary>
    private List<string> BackupFiles()
    {
        var prefix = _fileName + CorruptSuffix;
        try
        {
            var folder = Path.GetDirectoryName(_path)!;
            if (!Directory.Exists(folder))
                return [];
            var files = new List<string>();
            foreach (var path in Directory.EnumerateFiles(folder, prefix + "*"))
            {
                if (Path.GetFileName(path).StartsWith(prefix, StringComparison.Ordinal))
                    files.Add(path);
            }

            return files;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Sort key of a kept-aside copy's name: its UTC stamp and same-second counter (<c>-n</c>). Names that don't
    /// follow that pattern sort first.
    /// </summary>
    private (string Stamp, int N) BackupOrder(string name)
    {
        var rest = name[(_fileName.Length + CorruptSuffix.Length)..];
        if (rest.Length < StampLength
            || !DateTime.TryParseExact(rest[..StampLength], StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return (string.Empty, 0);

        var stamp = rest[..StampLength];
        var counter = rest[StampLength..];
        if (counter.Length == 0)
            return (stamp, 0);
        return counter[0] == '-' && int.TryParse(counter[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? (stamp, n)
            : (string.Empty, 0);
    }

    /// <summary>
    /// Keeps the bad file aside, never overwritten: moved (it didn't parse), or copied when
    /// <paramref name="keepOriginal"/> (only some values were invalid; the caller rewrites it). False when that failed too.
    /// </summary>
    private bool Quarantine(bool keepOriginal)
    {
        var stamp = _utcNow().ToString(StampFormat, CultureInfo.InvariantCulture);
        var target = $"{_path}{CorruptSuffix}{stamp}";
        for (var n = 1; File.Exists(target); n++)
            target = $"{_path}{CorruptSuffix}{stamp}-{n}";

        try
        {
            if (keepOriginal)
            {
                File.Copy(_path, target);
                _warn($"WindUpKey: some entries in {_fileName} could not be read; kept a copy as {Path.GetFileName(target)} and dropped only the invalid values");
            }
            else
            {
                File.Move(_path, target);
                _warn($"WindUpKey: {_fileName} could not be read; kept it as {Path.GetFileName(target)} and started a new one");
            }

            return true;
        }
        catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && keepOriginal)
        {
            // Its valid entries are still used; it just isn't changed until a copy can be kept.
            WarnOnce("copy-aside", $"WindUpKey: some entries in {_fileName} could not be read, and it could not be copied aside ({ex.GetType().Name}); using its valid entries without changing it");
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MarkUnavailable("quarantine", $"WindUpKey: {_fileName} could not be read or moved aside ({ex.GetType().Name}); relay identity unavailable for now");
            return false;
        }
    }

    // ---------- writing ----------

    private enum UpdateOutcome
    {
        Saved,
        NoChange,
        ReadOnly,
        NotWritable,
        Unavailable,
    }

    /// <summary>
    /// Lock, re-read, merge one character, replace atomically, unlock. <paramref name="change"/> sees that
    /// character's entry as it is on disk now and returns the new entry, or null to change nothing. Afterwards
    /// the in-memory view is what is on disk; a failed save leaves the file and the view without the change. A
    /// partly invalid file that couldn't be copied aside is read but never changed. When the lock file can't be
    /// opened for writing, the file is read without the lock and never changed: no change is needed, or NotWritable.
    /// </summary>
    private UpdateOutcome Update(string key, Func<Entry?, Entry?> change)
    {
        using var held = AcquireLock(out var lockNotWritable);
        if (held is null && !lockNotWritable)
            return UpdateOutcome.Unavailable;

        var disk = held is not null ? ReadRecovered() : ReadWithoutLock();
        IdentityFile file;
        switch (disk.Status)
        {
            case DiskStatus.Ok or DiskStatus.Salvaged:
                file = disk.File!;
                break;
            case DiskStatus.Missing:
                file = NewFile();
                break;
            case DiskStatus.Newer:
                Accept(disk);
                return UpdateOutcome.ReadOnly;
            default:
                return UpdateOutcome.Unavailable;
        }

        file.Characters ??= new SortedDictionary<string, Entry?>(StringComparer.Ordinal);
        file.Characters.TryGetValue(key, out var existing);
        var updated = change(existing);
        if (updated is null)
        {
            Accept(disk with { File = file });
            return UpdateOutcome.NoChange;
        }

        if (held is null)
        {
            // Read without the lock: never written from (another client may be writing it right now).
            Accept(disk);
            return UpdateOutcome.NotWritable;
        }

        if (disk.Status == DiskStatus.Salvaged)
        {
            // Writing would replace the only copy of the invalid values: only reads work until it can be kept aside.
            Accept(disk);
            _unavailable = true;
            return UpdateOutcome.Unavailable;
        }

        // Decision 30: informational only, so a kept-aside copy can be matched to the key the UI shows.
        var pairingKey = PairingKeyUtil.FromContentIdHex(key);
        if (PairingKeyUtil.IsValid(pairingKey))
            updated.PairingKey = pairingKey;

        // Rollback is free: the view is only replaced from the file once it is saved.
        var before = existing;
        file.Characters[key] = updated;
        file.Version = CurrentVersion;
        if (!TryWrite(file))
        {
            if (before is null)
                file.Characters.Remove(key);
            else
                file.Characters[key] = before;
            SetView(new DiskState(DiskStatus.Ok, file, null).Characters(), disk.NotOnDisk);
            return _notWritable ? UpdateOutcome.NotWritable : UpdateOutcome.Unavailable;
        }

        Accept(new DiskState(DiskStatus.Ok, file, null));
        return UpdateOutcome.Saved;
    }

    /// <summary>
    /// Replaces the file atomically (temp file, then move). A save refused for access first clears a read-only
    /// attribute on the file or a leftover temp file (for example after copying pluginConfigs from another PC) and
    /// is tried once more. False when it failed: refused for access, the store is <see cref="RelayIdentityStoreStatus.NotWritable"/>
    /// (existing secrets still work); any other error makes it unavailable for a while.
    /// </summary>
    private bool TryWrite(IdentityFile file)
    {
        var clearedReadOnly = false;
        while (true)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                using (var stream = new FileStream(_tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, file, JsonOptions);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(_tempPath, _path, overwrite: true);
                _warnedStates.Clear();
                return true;
            }
            catch (UnauthorizedAccessException ex)
            {
                // Non-short-circuiting: both may carry the attribute.
                if (!clearedReadOnly && (TryClearReadOnly(_tempPath) | TryClearReadOnly(_path)))
                {
                    clearedReadOnly = true;
                    continue;
                }

                _notWritable = true;
                _unavailable = false;
                WarnOnce(SaveAccessState, $"WindUpKey: could not save {_fileName} ({ex.GetType().Name}): it, its temp file or its folder is read-only or not writable; existing relay identities still work, but nothing can be saved until it is");
            }
            catch (IOException ex)
            {
                _notWritable = false;
                MarkUnavailable(SaveState, $"WindUpKey: could not save {_fileName} ({ex.GetType().Name})");
            }

            try
            {
                File.Delete(_tempPath);
            }
            catch (Exception)
            {
                // Best effort; the next save overwrites it.
            }

            return false;
        }
    }

    /// <summary>
    /// The cross-process write lock: an exclusive handle on the lock file, retried briefly. Only the open handle
    /// counts (the OS drops it when a process exits), so a lock file left behind never blocks anyone. Null when
    /// another client kept it for longer than the lock wait (<see cref="DefaultLockWait"/> unless configured), or
    /// right away when the lock file can't be opened for writing at all (<paramref name="notWritable"/>): a read-only
    /// attribute that can't be cleared, or permissions. Waiting wouldn't help then; the caller reads without the lock.
    /// </summary>
    private FileStream? AcquireLock(out bool notWritable)
    {
        notWritable = false;
        var clearedReadOnly = false;
        var deadline = DateTime.UtcNow + _lockWait;
        while (true)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_lockPath)!);
                return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (UnauthorizedAccessException ex)
            {
                // Not another client (that is a sharing violation, an IOException): the lock file or its folder refuses
                // writing. A read-only attribute (pluginConfigs copied from another PC) is cleared, then tried once more.
                if (!clearedReadOnly && TryClearReadOnly(_lockPath))
                {
                    clearedReadOnly = true;
                    continue;
                }

                notWritable = true;
                WarnOnce("lock-access", $"WindUpKey: could not open {Path.GetFileName(_lockPath)} for writing ({ex.GetType().Name}): it or its folder is read-only or not writable, not in use by another client; reading the relay identity file without it, and nothing can be saved until it is writable");
                return null;
            }
            catch (IOException ex)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    MarkUnavailable("lock", $"WindUpKey: {Path.GetFileName(_lockPath)} is locked by another game client ({ex.GetType().Name}); relay identity unavailable for now, retrying");
                    return null;
                }
            }

            Thread.Sleep(LockRetryInterval);
        }
    }

    /// <summary>Clears the read-only attribute of <paramref name="path"/>, if it has one. True when it did.</summary>
    private bool TryClearReadOnly(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) == 0 || (attributes & FileAttributes.Directory) != 0)
                return false;
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            WarnOnce("cleared-read-only", $"WindUpKey: cleared the read-only attribute of {Path.GetFileName(path)} so the relay identity can be saved");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void MarkUnavailable(string state, string message)
    {
        _unavailable = true;
        // The UI asks every frame: don't wait on the lock again until the retry delay (or ResetRetryDelay).
        _nextLoadAttemptUtc = _utcNow() + LoadRetryDelay;
        WarnOnce(state, message);
    }

    /// <summary>Logs each unavailable/failure state once, not on every retry.</summary>
    private void WarnOnce(string state, string message)
    {
        if (_warnedStates.Add(state))
            _warn(message);
    }

    // ---------- file model ----------

    private enum DiskStatus
    {
        Ok,
        Missing,
        Newer,

        /// <summary>A current-version file where some entries failed validation; they are already dropped from it.</summary>
        Partial,

        /// <summary>
        /// A partly invalid file that couldn't be copied aside: its valid entries are used, but it is never written
        /// (that would lose the only copy of the rest).
        /// </summary>
        Salvaged,
        Corrupt,
        Unavailable,
    }

    /// <summary>One read of the file: the parsed file (current version) or the usable entries (newer version).</summary>
    private sealed record DiskState(DiskStatus Status, IdentityFile? File, Dictionary<string, Entry>? NewerEntries)
    {
        /// <summary>For <see cref="DiskStatus.Partial"/>: the characters whose entries lost a value, or were dropped.</summary>
        public IReadOnlySet<string>? Damaged { get; init; }

        /// <summary>For <see cref="DiskStatus.Partial"/>: the file's content as read, to keep a copy of it once.</summary>
        public string? Text { get; init; }

        /// <summary>The view differs from the file on disk because rewriting it failed; retried on the next read.</summary>
        public bool Unsaved { get; init; }

        /// <summary>Read without the lock (its file can't be opened for writing): nothing may be written from it.</summary>
        public bool Unlocked { get; init; }

        /// <summary>
        /// The characters whose secret in this view was restored from memory but isn't on disk, because the file
        /// couldn't be copied aside or rewritten (<see cref="DiskStatus.Salvaged"/>, <see cref="Unsaved"/>).
        /// </summary>
        public IReadOnlySet<string>? NotOnDisk { get; init; }

        public static readonly DiskState Empty = new(DiskStatus.Missing, null, null);
        public static readonly DiskState Corrupt = new(DiskStatus.Corrupt, null, null);
        public static readonly DiskState Unavailable = new(DiskStatus.Unavailable, null, null);

        /// <summary>A copy of the entries, keyed by normalized ContentId.</summary>
        public Dictionary<string, Entry> Characters()
        {
            var characters = new Dictionary<string, Entry>(StringComparer.Ordinal);
            if (NewerEntries is not null)
            {
                foreach (var (id, entry) in NewerEntries)
                    characters[id] = entry;
            }

            foreach (var (id, entry) in File?.Characters ?? new Dictionary<string, Entry?>())
            {
                if (entry is not null)
                    characters[id] = entry;
            }

            return characters;
        }
    }

    /// <summary>The file as read, with each entry still raw (see <see cref="Validate"/>).</summary>
    private sealed class StoredFile
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("characters")]
        public Dictionary<string, JsonElement>? Characters { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
    }

    private sealed class IdentityFile
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = CurrentVersion;

        [JsonPropertyName("characters")]
        public IDictionary<string, Entry?>? Characters { get; set; }

        /// <summary>Fields this build doesn't know; written back unchanged.</summary>
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
    }

    private sealed class Entry
    {
        [JsonPropertyName("secret")]
        public string? Secret { get; set; }

        [JsonPropertyName("previous")]
        public string? Previous { get; set; }

        /// <summary>
        /// The character's pairing key, recorded on each write for that character (decision 30), so a kept-aside copy
        /// can be matched to the key the UI shows. Informational only: never used to authenticate, never sent.
        /// </summary>
        [JsonPropertyName("pairingKey")]
        public string? PairingKey { get; set; }

        /// <summary>Fields this build doesn't know; written back unchanged.</summary>
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
    }
}
