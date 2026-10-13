#if WINDUP_TESTING || WINDUP_SELFTEST_UNITS
using System;
using System.Collections.Generic;
using System.Linq;
using WindUpKey.Protocol;
using WindUpKey.Services;

namespace WindUpKey.SelfTest;

/// <summary>
/// The client's own state as one framework-thread snapshot (04-D6 "no game or config state off the framework thread").
/// Keys and URLs are held only to compare and to address probes; the report shows them redacted (04-D3). Secrets and
/// the token never enter it: the host compares them on the framework thread and passes only the answers.
/// </summary>
public sealed record SessionSnapshot
{
    /// <summary>This character's pairing key (compare and address only; shown as AB******).</summary>
    public string OwnKey { get; init; } = string.Empty;

    /// <summary><see cref="OwnKey"/> equals the key derived from the active ContentId.</summary>
    public bool PairingKeyMatchesContentId { get; init; }

    /// <summary>
    /// 04-D11: the key the live session registered with, while its register is accepted (else null). The own-key probes
    /// address this key, never <see cref="OwnKey"/>.
    /// </summary>
    public string? RegisteredKey { get; init; }

    /// <summary>04-D11: the key derived from the active ContentId, or null when there is no valid one.</summary>
    public string? ContentIdKey { get; init; }

    /// <summary>The configured relay token equals the compiled one (compared on the framework thread, never shown).</summary>
    public bool TokenMatchesCompiled { get; init; }

    public IReadOnlyList<string> PartnerKeys { get; init; } = [];

    public IReadOnlyList<string> PendingKeys { get; init; } = [];

    public IReadOnlyList<string> PendingRemovalKeys { get; init; } = [];

    public IReadOnlyList<string> OwnedDollKeys { get; init; } = [];

    /// <summary>The <c>peerKeys</c> of this connection's pairSync, or null before one arrived.</summary>
    public IReadOnlyList<string>? RelayPeerKeys { get; init; }

    public bool Connected { get; init; }

    /// <summary>The live session's host URL while connected, else null.</summary>
    public string? ConnectedUrl { get; init; }

    public bool RegisterAccepted { get; init; }

    public SessionInvariants Invariants { get; init; }

    public string? AuthBlockCode { get; init; }

    public string? LastSuccessfulRelayUrl { get; init; }

    public IReadOnlyList<string> CompiledUrls { get; init; } = [];

    public bool ShouldSuspendMovementLocks { get; init; }

    /// <summary>Consent and wind stamps more than 5 minutes after the local clock (02-D6).</summary>
    public int FutureStampCount { get; init; }

    /// <summary>06-B1: the active character's stored safeword pause end (null: none), as stored.</summary>
    public DateTimeOffset? SafewordPausedUntilUtc { get; init; }

    /// <summary>06-B1: Hardcore is on for the active character.</summary>
    public bool HardcoreMode { get; init; }

    /// <summary>When the snapshot was taken (UTC), to judge <see cref="SafewordPausedUntilUtc"/>.</summary>
    public DateTimeOffset TakenAtUtc { get; init; }

    public RelayIdentityDiagnosis? Identity { get; init; }

    /// <summary>The Copy button has a valid identity to give.</summary>
    public bool CopyAvailable { get; init; }

    /// <summary>Checking an import of the current value says it's already this character's (no write).</summary>
    public RelayIdentityImport? SameValueImportCheck { get; init; }
}

/// <summary>
/// Judges a <see cref="SessionSnapshot"/>: the extra checks of planning §5 (<c>X-*</c>) and the automated parts of the
/// partial checklist items that need only local state (01-1, 01-2, 01-4, 01-10, 01-11, 02-3, 02-4). Pure.
/// </summary>
public static class SessionFactsJudge
{
    public static IReadOnlyList<CheckResult> Judge(SessionSnapshot s)
    {
        var results = new List<CheckResult>();
        var identity = s.Identity;

        // ---- X: identity file, entry, backups ----
        if (identity is null)
        {
            results.Add(CheckResult.Skip("X-identity-file", "no character profile yet"));
            results.Add(CheckResult.Skip("X-identity-entry", "no character profile yet"));
        }
        else
        {
            var where = $"{identity.FileName} (pluginConfigs), version {identity.Version}, store {identity.Status}";
            results.Add(!identity.FileExists ? CheckResult.Fail("X-identity-file", $"{identity.FileName} not found in pluginConfigs")
                : !identity.OutsidePluginFolder ? CheckResult.Fail("X-identity-file", $"{where}: inside the plugin's own folder, which reset deletes")
                : identity.Status == RelayIdentityStoreStatus.Available ? CheckResult.Pass("X-identity-file", where)
                : identity.Status == RelayIdentityStoreStatus.Unavailable ? CheckResult.Fail("X-identity-file", where)
                : CheckResult.Warn("X-identity-file", where));

            var previous = identity.PreviousValid ? "a valid previous identity" : "no previous identity";
            results.Add(!identity.HasEntry ? CheckResult.Fail("X-identity-entry", "no entry for this character")
                : !identity.SecretValid ? CheckResult.Fail("X-identity-entry", "the entry has no valid secret")
                : identity.PairingKey == RelayIdentityKeyRecord.Differs ? CheckResult.Fail("X-identity-entry", "the entry's pairingKey doesn't match this character's key")
                : identity.PairingKey == RelayIdentityKeyRecord.Absent ? CheckResult.Warn("X-identity-entry", $"valid secret, {previous}; pairingKey not recorded yet (written on the next write)")
                : CheckResult.Pass("X-identity-entry", $"valid secret, {previous}, pairingKey matches"));

            results.Add(identity.BackupCount > 0
                ? CheckResult.Warn("X-identity-backups", $"{identity.BackupCount} .corrupt-* cop{(identity.BackupCount == 1 ? "y" : "ies")} next to the identity file")
                : CheckResult.Pass("X-identity-backups", "no .corrupt-* copies"));
        }

        // ---- X: key, token, host, register, stamps, lock safety, pending ----
        results.Add(s.PairingKeyMatchesContentId
            ? CheckResult.Pass("X-pairing-key", $"{SelfTestRedact.Key(s.OwnKey)} matches the ContentId-derived key")
            : CheckResult.Fail("X-pairing-key", "the pairing key doesn't match the ContentId-derived key"));
        results.Add(s.TokenMatchesCompiled
            ? CheckResult.Pass("X-relay-token", "equals the compiled token")
            : CheckResult.Fail("X-relay-token", "differs from the compiled token"));

        if (!s.Connected || s.ConnectedUrl is not { } url)
        {
            results.Add(CheckResult.Skip("X-host", "not connected"));
        }
        else
        {
            var compiled = s.CompiledUrls.Any(u => string.Equals(u, url, StringComparison.OrdinalIgnoreCase));
            var sticky = string.Equals(s.LastSuccessfulRelayUrl, url, StringComparison.OrdinalIgnoreCase);
            results.Add(!compiled ? CheckResult.Fail("X-host", $"connected to {SelfTestRedact.Host(url)}, which isn't a compiled host")
                : !sticky ? CheckResult.Warn("X-host", $"connected to {SelfTestRedact.Host(url)}, but the saved last host is {SelfTestRedact.Host(s.LastSuccessfulRelayUrl)}")
                : CheckResult.Pass("X-host", $"connected to compiled host {SelfTestRedact.Host(url)}, saved as the last host"));
        }

        results.Add(s.RegisterAccepted ? CheckResult.Pass("X-register", "the relay accepted the register")
            : s.AuthBlockCode is { } code ? CheckResult.Fail("X-register", $"the relay rejected this session ({code})")
            : CheckResult.Fail("X-register", s.Connected ? "connected, but the register isn't accepted" : "not connected"));

        results.Add(s.FutureStampCount == 0
            ? CheckResult.Pass("X-stamps", "no future-dated consent or wind stamps")
            : CheckResult.Warn("X-stamps", $"{s.FutureStampCount} stamp(s) dated more than 5 minutes ahead (reset at the next profile load, 02-D22)"));

        results.Add(!s.Connected ? CheckResult.Skip("X-suspend", "not connected")
            : !s.ShouldSuspendMovementLocks ? CheckResult.Pass("X-suspend", "locks are not suspended while connected")
            : CheckResult.Fail("X-suspend", "locks are suspended although the relay is connected"));

        results.Add(SafewordPauseState(s));

        results.Add(CheckResult.Info("X-pending", $"{s.PartnerKeys.Count} partner(s), {s.PendingKeys.Count} pending pair request(s), {s.PendingRemovalKeys.Count} queued unpair(s), {s.OwnedDollKeys.Count} owned doll(s)"));

        // ---- partial checklist items, local part ----
        results.Add(FirstConnect(s));
        results.Add(PairsSurvive(s));
        results.Add(IdentitySection(s));
        results.Add(identity is null ? CheckResult.Skip("01-10", "no character profile yet")
            : identity.FileExists && identity.OutsidePluginFolder ? CheckResult.Pass("01-10", $"{identity.FileName} is outside pluginConfigs/WindUpKey/, so reset plugin data can't delete it")
            : CheckResult.Fail("01-10", "the identity file is missing or inside the plugin's own folder"));
        results.Add(identity is null ? CheckResult.Skip("01-11", "no character profile yet")
            : !identity.FileExists || !identity.OutsidePluginFolder ? CheckResult.Fail("01-11", "the identity file is missing or inside the plugin's own folder")
            : identity.Version == RelayIdentityFileVersion.Newer ? CheckResult.Warn("01-11", "the identity file is from a newer build: used read-only")
            : CheckResult.Pass("01-11", "the identity is in its own file outside the plugin config, at a version this build reads"));
        results.Add(!s.RegisterAccepted ? CheckResult.Skip("02-3", "the register isn't accepted, so queued unpairs aren't sent yet")
            : s.PendingRemovalKeys.Count == 0 ? CheckResult.Pass("02-3", "no queued unpair left pending after the register was accepted")
            : CheckResult.Warn("02-3", $"{s.PendingRemovalKeys.Count} queued unpair(s) still pending after the register was accepted"));
        var partners = new HashSet<string>(s.PartnerKeys.Select(WireLimits.NormalizeKey), StringComparer.Ordinal);
        // The debug self-pair owns this character's own key (SyncDebugSelfOwnership): not a stale entry.
        partners.Add(WireLimits.NormalizeKey(s.OwnKey));
        var stale = s.OwnedDollKeys.Count(k => !partners.Contains(WireLimits.NormalizeKey(k)));
        results.Add(stale == 0
            ? CheckResult.Pass("02-4", $"every owned doll ({s.OwnedDollKeys.Count}) is a partner")
            : CheckResult.Warn("02-4", $"{stale} owned doll(s) aren't partners (stale Owner entries)"));
        return results;
    }

    /// <summary>
    /// 06-B1: the pause is consistent: none, ended, or ending within 24 h, and never kept in Hardcore. The timer tidies
    /// an ended or out-of-range one on its next tick, so a FAIL means that didn't happen.
    /// </summary>
    private static CheckResult SafewordPauseState(SessionSnapshot s)
    {
        const string id = "X-safeword-pause";
        if (s.SafewordPausedUntilUtc is not { } stored)
            return CheckResult.Pass(id, "remote control isn't paused");
        if (s.HardcoreMode)
            return CheckResult.Fail(id, "a safeword pause is stored while Hardcore is on");
        if (stored - s.TakenAtUtc > SafewordPause.MaxLength)
            return CheckResult.Fail(id, "the stored safeword pause ends more than 24 h from now");
        return SafewordPause.IsActive(stored, s.TakenAtUtc, hardcore: false)
            ? CheckResult.Pass(id, $"remote control is paused for about {Math.Max(1, (int)Math.Ceiling((stored - s.TakenAtUtc).TotalHours))} more hour(s)")
            : CheckResult.Pass(id, "the stored safeword pause has ended");
    }

    private static CheckResult FirstConnect(SessionSnapshot s)
    {
        const string id = "01-1";
        if (s.Identity is not { } identity)
            return CheckResult.Skip(id, "no character profile yet");
        if (!identity.FileExists || !identity.OutsidePluginFolder)
            return CheckResult.Fail(id, "the identity file isn't next to the WindUpKey folder");
        if (!identity.HasEntry || !identity.SecretValid)
            return CheckResult.Fail(id, "no valid entry for this character");
        if (!s.RegisterAccepted)
            return CheckResult.Fail(id, "the register isn't accepted");
        return identity.PairingKey switch
        {
            RelayIdentityKeyRecord.Matches => CheckResult.Pass(id, "identity file next to the WindUpKey folder, entry with pairingKey, register accepted"),
            RelayIdentityKeyRecord.Absent => CheckResult.Warn(id, "the entry doesn't record pairingKey yet (an older entry)"),
            _ => CheckResult.Fail(id, "the entry's pairingKey doesn't match"),
        };
    }

    private static CheckResult PairsSurvive(SessionSnapshot s)
    {
        const string id = "01-2";
        if (s.RelayPeerKeys is not { } relay)
            return CheckResult.Skip(id, "no pairSync on this connection yet");
        var atRelay = new HashSet<string>(relay.Select(WireLimits.NormalizeKey), StringComparer.Ordinal);
        var removing = new HashSet<string>(s.PendingRemovalKeys.Select(WireLimits.NormalizeKey), StringComparer.Ordinal);
        var own = WireLimits.NormalizeKey(s.OwnKey);
        var local = s.PartnerKeys.Select(WireLimits.NormalizeKey).Where(k => k != own && !removing.Contains(k)).Distinct().ToList();
        var missing = local.Count(k => !atRelay.Contains(k));
        return missing == 0
            ? CheckResult.Pass(id, $"all {local.Count} local partner(s) are paired on the relay")
            : CheckResult.Warn(id, $"{missing} of {local.Count} local partner(s) aren't paired on this relay host (restored once both partners have been online, 02 migration)");
    }

    private static CheckResult IdentitySection(SessionSnapshot s)
    {
        const string id = "01-4";
        if (s.Identity is null)
            return CheckResult.Skip(id, "no character profile yet");
        if (!s.CopyAvailable)
            return CheckResult.Fail(id, "Copy has no valid identity to give");
        return s.SameValueImportCheck switch
        {
            RelayIdentityImport.Unchanged => CheckResult.Pass(id, "Copy has a valid identity, and importing it is recognised as already this character's"),
            null => CheckResult.Skip(id, "the import check couldn't run"),
            var other => CheckResult.Fail(id, $"importing the current value says {other}, expected Unchanged"),
        };
    }
}
#endif
