using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using Lumina.Excel.Sheets;

namespace WindUpKey.Services;

/// <summary>Runs in-game text commands / emotes on the framework thread.</summary>
public sealed unsafe class GameCommandRunner(
    IPluginLog log,
    IDataManager data,
    IUnlockState unlockState,
    Configuration config)
{
    /// <summary>
    /// Emote sheet row for Sit (<c>/sit</c>, EmoteMode 2). 06-C6: it was 13, which is Doze (<c>/doze</c>,
    /// <see cref="LockEmoteRule.DozeEmoteId"/>): that put Doze in the lock emote list and read a doze as a sit.
    /// </summary>
    public const ushort SitEmoteId = 50;

    private const int UnlockedEmotesCacheMs = 5000;

    private IReadOnlyList<(ushort Id, string Name)>? _unlockedLoopingEmotesCache;
    private long _unlockedLoopingEmotesCacheAtMs = long.MinValue;

    /// <summary>
    /// Unlocked looping emotes the player can use (EmoteMode set, has text command, unlocked).
    /// Sit and Ground Sit are always included (they loop via sit state, not EmoteMode). 06-C6: emotes that can't be
    /// the lock emote (<see cref="LockEmoteRule"/>: Doze and other position loops) are left out.
    /// Sorted by display name. Cached briefly (~5s) to avoid rescanning the Emote sheet every UI draw.
    /// </summary>
    public IReadOnlyList<(ushort Id, string Name)> GetUnlockedLoopingEmotes()
    {
        var nowMs = Environment.TickCount64;
        if (_unlockedLoopingEmotesCache is not null
            && nowMs - _unlockedLoopingEmotesCacheAtMs < UnlockedEmotesCacheMs)
            return _unlockedLoopingEmotesCache;

        var sheet = data.GetExcelSheet<Emote>();
        if (sheet is null)
            return [];

        var list = new List<(ushort Id, string Name)>();
        var seen = new HashSet<ushort>();
        foreach (var emote in sheet)
        {
            var name = emote.Name.ToString();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var forceSit = IsSitOrGroundSitEmote(emote, name);
            if (!LockEmoteRule.IsAllowed((ushort)emote.RowId, ConditionMode(emote), forceSit))
                continue;

            if (!forceSit)
            {
                if (!IsLoopingUsableEmote(emote))
                    continue;
                if (!IsEmoteUnlocked(emote))
                    continue;
            }

            var id = (ushort)emote.RowId;
            if (!seen.Add(id))
                continue;

            list.Add((id, name));
        }

        // Absolute fallback if localization/command lookup missed them.
        TryAddForcedSitEmote(sheet, list, seen, SitEmoteId, "Sit");
        TryAddForcedSitEmote(sheet, list, seen, CharacterProfile.GroundSitEmoteId, "Sit on Ground");

        _unlockedLoopingEmotesCache = list
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        _unlockedLoopingEmotesCacheAtMs = nowMs;
        return _unlockedLoopingEmotesCache;
    }

    private static bool IsSitOrGroundSitEmote(Emote emote, string name)
    {
        if (name.Equals("Sit", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Sit on Ground", StringComparison.OrdinalIgnoreCase))
            return true;

        return EmoteUsesTextCommand(emote, "sit") || EmoteUsesTextCommand(emote, "groundsit");
    }

    /// <summary>
    /// True when this emote's text command (or alias) is /sit or /groundsit (slash optional).
    /// </summary>
    private static bool EmoteUsesTextCommand(Emote emote, string commandWithoutSlash)
    {
        if (emote.TextCommand.RowId == 0)
            return false;

        try
        {
            var tc = emote.TextCommand.Value;
            return CommandTokenEquals(tc.Command.ToString(), commandWithoutSlash)
                   || CommandTokenEquals(tc.ShortCommand.ToString(), commandWithoutSlash)
                   || CommandTokenEquals(tc.Alias.ToString(), commandWithoutSlash)
                   || CommandTokenEquals(tc.ShortAlias.ToString(), commandWithoutSlash);
        }
        catch
        {
            return false;
        }
    }

    private static bool CommandTokenEquals(string? raw, string commandWithoutSlash)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var token = raw.Trim();
        if (token.StartsWith('/'))
            token = token[1..];

        // TextCommand may be "sit" or "sit motion" — match first token only.
        var space = token.IndexOf(' ');
        if (space > 0)
            token = token[..space];

        return token.Equals(commandWithoutSlash, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryAddForcedSitEmote(
        Lumina.Excel.ExcelSheet<Emote> sheet,
        List<(ushort Id, string Name)> list,
        HashSet<ushort> seen,
        ushort emoteId,
        string fallbackName)
    {
        if (!seen.Add(emoteId))
            return;

        var name = fallbackName;
        if (sheet.TryGetRow(emoteId, out var emote))
        {
            var sheetName = emote.Name.ToString();
            if (!string.IsNullOrWhiteSpace(sheetName))
                name = sheetName;
        }

        list.Add((emoteId, name));
    }

    /// <summary>Display name for an emote row, or null if missing.</summary>
    public string? GetEmoteName(ushort emoteId)
    {
        var sheet = data.GetExcelSheet<Emote>();
        if (sheet is null || !sheet.TryGetRow(emoteId, out var emote))
            return null;

        var name = emote.Name.ToString();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>
    /// Play the configured lock emote (fallback Ground Sit). Returns false if the game is not ready yet.
    /// </summary>
    public bool TryExecuteLockEmote()
    {
        var id = ResolveLockEmoteId();
        return TryExecuteEmote(id);
    }

    private ushort ResolveLockEmoteId()
    {
        var id = config.EffectiveLockEmoteId;
        if (id != CharacterProfile.GroundSitEmoteId && IsEmoteIdUnlockedAndLooping(id))
            return id;

        return CharacterProfile.GroundSitEmoteId;
    }

    /// <summary>
    /// 06-C6: an excluded emote (<see cref="LockEmoteRule"/>) falls back to Ground Sit here like one that isn't unlocked;
    /// the stored value isn't touched.
    /// </summary>
    private bool IsEmoteIdUnlockedAndLooping(ushort emoteId)
    {
        var sheet = data.GetExcelSheet<Emote>();
        if (sheet is null || !sheet.TryGetRow(emoteId, out var emote))
            return emoteId is SitEmoteId or CharacterProfile.GroundSitEmoteId;

        var name = emote.Name.ToString();
        var sit = IsSitOrGroundSitEmote(emote, name ?? string.Empty);
        if (!LockEmoteRule.IsAllowed(emoteId, ConditionMode(emote), sit))
            return false;
        if (sit)
            return true;

        return IsLoopingUsableEmote(emote) && IsEmoteUnlocked(emote);
    }

    /// <summary>
    /// 06-C6: whether <paramref name="emoteId"/> can be the lock emote (<see cref="LockEmoteRule.IsAllowed"/>), from the
    /// Emote sheet; a row the sheet doesn't have is left to the runtime fallback. Sheet reads only.
    /// </summary>
    public bool IsAllowedLockEmote(ushort emoteId)
    {
        var sheet = data.GetExcelSheet<Emote>();
        if (sheet is null || !sheet.TryGetRow(emoteId, out var emote))
            return LockEmoteRule.IsAllowed(emoteId, 0, emoteId is SitEmoteId or CharacterProfile.GroundSitEmoteId);

        return LockEmoteRule.IsAllowed(emoteId, ConditionMode(emote), IsSitOrGroundSitEmote(emote, emote.Name.ToString()));
    }

    /// <summary>The emote's EmoteMode.ConditionMode (11 for a position loop); 0 without an EmoteMode.</summary>
    private static byte ConditionMode(Emote emote) =>
        emote.EmoteMode.RowId == 0 ? (byte)0 : emote.EmoteMode.ValueNullable?.ConditionMode ?? 0;

    private static bool IsLoopingUsableEmote(Emote emote) =>
        emote.EmoteMode.RowId != 0 && emote.TextCommand.RowId != 0;

    private bool IsEmoteUnlocked(Emote emote)
    {
        try
        {
            return unlockState.IsEmoteUnlocked(emote);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "WindUpKey: IUnlockState.IsEmoteUnlocked failed for {EmoteId}; falling back to UIState", emote.RowId);
            try
            {
                var uiState = UIState.Instance();
                return uiState != null && uiState->IsEmoteUnlocked((ushort)emote.RowId);
            }
            catch (Exception ex2)
            {
                log.Warning(ex2, "WindUpKey: UIState.IsEmoteUnlocked failed for {EmoteId}", emote.RowId);
                return false;
            }
        }
    }

    private bool TryExecuteEmote(ushort emoteId)
    {
        try
        {
            var manager = EmoteManager.Instance();
            if (manager == null)
                return false;

            if (!manager->CanExecuteEmote(emoteId))
                return false;

            manager->ExecuteEmote(emoteId);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "WindUpKey: failed to ExecuteEmote {EmoteId}", emoteId);
            return true; // don't spin forever on hard failures
        }
    }

    /// <summary>Returns false if the UI shell is not ready yet (caller may retry).</summary>
    public bool TryExecute(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return true;

        try
        {
            var shell = RaptureShellModule.Instance();
            var ui = UIModule.Instance();
            if (shell == null || ui == null)
                return false;

            var str = Utf8String.FromString(command.Trim());
            try
            {
                shell->ExecuteCommandInner(str, ui);
            }
            finally
            {
                str->Dtor(true);
            }

            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "WindUpKey: failed to execute {Command}", command);
            return true; // don't spin forever on hard failures
        }
    }

    public void Execute(string command) => TryExecute(command);
}
