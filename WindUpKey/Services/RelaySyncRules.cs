using System;
using System.Collections.Generic;
using System.Linq;
using WindUpKey.Protocol;

namespace WindUpKey.Services;

/// <summary>A change a pairing sync made on this PC, told to the player in chat.</summary>
public enum PairSyncChange
{
    /// <summary>A partner this PC didn't know (neither a partner nor a pending request made here).</summary>
    PartnerAdded,

    /// <summary>A synced consent row turned on <see cref="PairedPartner.IsOwner"/>.</summary>
    MadeOwner,

    /// <summary>A synced consent row turned on <see cref="PairedPartner.CanCallMe"/> (told only in builds with Call).</summary>
    AllowedCall,
}

/// <summary>A key a pairing sync looks at, and whether the sync may add it as a partner here.</summary>
/// <param name="PeerKey">The normalized partner key.</param>
/// <param name="MayAdd">True for keys listed in <c>peerKeys</c>; false for keys that only have a consent row.</param>
public readonly record struct PairSyncTarget(string PeerKey, bool MayAdd);

/// <summary>What <see cref="RelaySyncRules.ResetFutureLocalStamps"/> reset.</summary>
/// <param name="Partners">Partners whose consent flags were turned off and stamp cleared.</param>
/// <param name="WindStampCleared">The wind stamp was in the future: the caller clears it (keeping the expiry).</param>
public sealed record FutureStampReset(IReadOnlyList<PairedPartner> Partners, bool WindStampCleared)
{
    /// <summary>Whether anything was reset (and so needs saving).</summary>
    public bool Any => Partners.Count > 0 || WindStampCleared;
}

/// <summary>What <see cref="RelaySyncRules.RemovePartnerLocally"/> removed.</summary>
/// <param name="Partner">A paired partner with the key was removed.</param>
/// <param name="WasOwner">That partner was this character's owner (a doll unpairing its owner).</param>
/// <param name="OwnedDoll">The key was one of this character's owned dolls (an owner unpairing its doll).</param>
public readonly record struct LocalPartnerRemoval(bool Partner, bool WasOwner, bool OwnedDoll)
{
    /// <summary>Whether anything was removed (and so needs saving).</summary>
    public bool Any => Partner || OwnedDoll;
}

/// <summary>
/// What the client accepts from partners and from relay sync: presence replies, remote sync stamps, which sync
/// changes are announced, which keys a sync may add, resetting local stamps from the future, and what removing a
/// partner clears here. No Dalamud dependency.
/// </summary>
public static class RelaySyncRules
{
    /// <summary>
    /// Whether to answer a forwarded presence query from <paramref name="fromKey"/>, and the reply's
    /// <paramref name="stillPaired"/>. A partner gets <c>true</c>. Any other sender gets <c>false</c> (with
    /// <c>online: true</c> and nothing else), so a half-unpair that never reached the relay heals: the relay only
    /// forwards between its own pairs, and an old relay tells anyone the online state anyway. The self-key is answered
    /// only when paired or in debug, as before.
    /// </summary>
    public static bool TryAnswerPresence(string fromKey, string localKey, bool debugEnabled, bool paired, out bool stillPaired)
    {
        stillPaired = false;
        if (!PairingKeyUtil.IsValid(fromKey) || !PairingKeyUtil.IsValid(localKey))
            return false;
        if (string.Equals(fromKey, localKey, StringComparison.Ordinal) && !paired && !debugEnabled)
            return false;

        stillPaired = paired;
        return true;
    }

    /// <summary>
    /// 08-D15: the <c>stillPaired</c> a presence reply carries. Unset (null) until this connection's register is accepted
    /// (its pairSync arrived): before that the local list may be empty (a second PC, a fresh install), and a
    /// <c>false</c> from it would take a real partner off the asker's list. An asker treats null as no answer, as it
    /// does the relay's own presence answer, which never carries <c>stillPaired</c>, online or offline. A <c>false</c>
    /// only removes the partner on the asker's side, never on the relay (08-D19).
    /// </summary>
    public static bool? PresenceStillPairedAnswer(bool stillPaired, bool registerAccepted) =>
        registerAccepted ? stillPaired : null;

    /// <summary>
    /// 08-T6 (D10): the error an owner-settings result leaves on the doll's snapshot. Normally none (the result is the
    /// doll's current values), but the re-query after a <c>safeword_paused</c> refusal keeps the "paused" message
    /// through its one result (<see cref="IsOwnerSettingsReQueryResult"/>).
    /// </summary>
    public static string? OwnerSettingsErrorAfterResult(string? previousError, bool keepErrorOnNextResult) =>
        keepErrorOnNextResult ? previousError : null;

    /// <summary>
    /// 08-D17: whether an owner-settings result is the one the re-query after a refusal waits for: the snapshot is
    /// marked to keep its error, and the result's RequestId is the re-query's. Any other result (an earlier query's,
    /// say) clears the error as usual, and a re-query not yet sent (no RequestId) matches nothing.
    /// </summary>
    public static bool IsOwnerSettingsReQueryResult(bool keepErrorOnNextResult, string? reQueryRequestId, string? resultRequestId) =>
        keepErrorOnNextResult
        && !string.IsNullOrEmpty(reQueryRequestId)
        && string.Equals(reQueryRequestId, resultRequestId, StringComparison.Ordinal);

    /// <summary>
    /// Reads a remote consent or wind stamp from relay sync. False when it's missing, unreadable, or more than
    /// <see cref="SyncStamp.MaxFutureSkew"/> after this PC's clock: such a row would win last-write-wins until then,
    /// so it's treated as absent.
    /// </summary>
    public static bool TryReadRemoteStamp(string? raw, DateTimeOffset localNow, out DateTimeOffset stamp) =>
        SyncStamp.TryParse(raw, out stamp) && !SyncStamp.IsTooFarAhead(stamp, localNow);

    /// <summary>
    /// The keys a <c>pairSync</c> looks at, normalized, valid, without <paramref name="localKey"/> and without
    /// duplicates: first those in <paramref name="peerKeys"/> (the relay's established pairs), which may add a
    /// partner, then those that only have a consent row, which never do. A consent row only updates a partner this PC
    /// already knows, or one added from <paramref name="peerKeys"/> in the same sync (02-D23).
    /// </summary>
    public static IReadOnlyList<PairSyncTarget> PairSyncTargets(
        IEnumerable<string?>? peerKeys,
        IEnumerable<string?>? consentKeys,
        string? localKey)
    {
        var self = PairingKeyUtil.Normalize(localKey);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var targets = new List<PairSyncTarget>();

        void Add(IEnumerable<string?>? keys, bool mayAdd)
        {
            foreach (var raw in keys ?? [])
            {
                var key = PairingKeyUtil.Normalize(raw);
                if (PairingKeyUtil.IsValid(key) && !string.Equals(key, self, StringComparison.Ordinal) && seen.Add(key))
                    targets.Add(new PairSyncTarget(key, mayAdd));
            }
        }

        Add(peerKeys, mayAdd: true);
        Add(consentKeys, mayAdd: false);
        return targets;
    }

    /// <summary>
    /// Resets local stamps dated more than <see cref="SyncStamp.MaxFutureSkew"/> after this PC's clock, which could
    /// have been forged and accepted by an older build, and would win last-write-wins until then (02-D22). Each
    /// such partner gets <see cref="PairedPartner.CanWindMe"/>, <see cref="PairedPartner.CanUnwindMe"/>,
    /// <see cref="PairedPartner.IsOwner"/> and <see cref="PairedPartner.CanCallMe"/> turned off and its
    /// <see cref="PairedPartner.ConsentUpdatedUtc"/> cleared. With no local stamp and the flags off, the next push
    /// leaves the partner out and the next sync takes a valid relay copy, so that copy restores them (02-D27). No
    /// <c>ownerRevoked</c> is sent for it. A future <paramref name="windUpdatedUtc"/> is only reported, for the caller
    /// to clear while keeping the expiry, so the relay's value wins if it has one.
    /// </summary>
    public static FutureStampReset ResetFutureLocalStamps(
        IEnumerable<PairedPartner> partners,
        DateTimeOffset? windUpdatedUtc,
        DateTimeOffset localNow)
    {
        var reset = new List<PairedPartner>();
        foreach (var partner in partners)
        {
            if (partner.ConsentUpdatedUtc is not { } stamp || !SyncStamp.IsTooFarAhead(stamp, localNow))
                continue;

            partner.CanWindMe = false;
            partner.CanUnwindMe = false;
            partner.IsOwner = false;
            partner.CanCallMe = false;
            partner.ConsentUpdatedUtc = null;
            reset.Add(partner);
        }

        var windCleared = windUpdatedUtc is { } wind && SyncStamp.IsTooFarAhead(wind, localNow);
        return new FutureStampReset(reset, windCleared);
    }

    /// <summary>
    /// The chat line for a partner whose permissions were reset (label: nickname, then Name@World, then key). The
    /// next sync restores them from a valid relay copy, if there is one (02-D27).
    /// </summary>
    public static string DescribeStampReset(string label) =>
        $"Permissions for {label} had an invalid timestamp; restored from the relay if it has a valid copy, otherwise turned off. Please check them.";

    /// <summary>
    /// Whether to push the doll's state after relay sync made or restored a pair here (02-D29): a
    /// <c>pairEstablished</c> for a partner kept on this PC, or a <c>pairSync</c> that added one. The relay's consent
    /// row for a pair it just made is missing or stale (a push sent before the pair existed was ignored), and the
    /// push also brings the doll's other PCs up to date. <paramref name="pairedHere"/> is false when this PC skipped
    /// the partner (unpaired here).
    /// </summary>
    public static bool PushesAfterPairing(bool isDoll, bool pairedHere) => isDoll && pairedHere;

    /// <summary>
    /// Removes <paramref name="peerKey"/> from this character's partners and from its owned dolls, whether the player
    /// unpaired it here (live or queued) or the partner's side did (02-D30). An owner's own unpair clears its ownership
    /// here too: the relay drops the doll's reply <c>ownerRevoked</c> once the pair is gone (02-D28). Nothing for an
    /// invalid key.
    /// </summary>
    public static LocalPartnerRemoval RemovePartnerLocally(
        List<PairedPartner> partners,
        List<OwnedDoll> ownedDolls,
        string? peerKey)
    {
        var key = PairingKeyUtil.Normalize(peerKey);
        if (!PairingKeyUtil.IsValid(key))
            return default;

        var wasOwner = partners.Any(p => p is { IsOwner: true } && IsKey(p.PartnerKey, key));
        var partner = partners.RemoveAll(p => p is not null && IsKey(p.PartnerKey, key)) > 0;
        var ownedDoll = ownedDolls.RemoveAll(d => d is not null && IsKey(d.DollKey, key)) > 0;
        return new LocalPartnerRemoval(partner, wasOwner, ownedDoll);
    }

    /// <summary>
    /// Whether sync changes are told in chat. Not before the profile has completed its initial setup, where
    /// everything synced is new.
    /// </summary>
    public static bool AnnouncesSyncChanges(bool hasCompletedInitialSetup) => hasCompletedInitialSetup;

    /// <summary>
    /// What a sync changed for one partner, in the order told. <paramref name="newToThisPc"/> is true when the partner
    /// was neither a partner nor a pending request on this PC before; the flags are before and after the sync.
    /// <paramref name="callFeature"/> is whether this build has the Call feature (<c>WINDUP_TESTING</c>): call
    /// rights are told only then.
    /// </summary>
    public static IReadOnlyList<PairSyncChange> Changes(
        bool newToThisPc,
        bool wasOwner,
        bool couldCall,
        bool isOwner,
        bool canCall,
        bool callFeature)
    {
        var changes = new List<PairSyncChange>();
        if (newToThisPc)
            changes.Add(PairSyncChange.PartnerAdded);
        if (!wasOwner && isOwner)
            changes.Add(PairSyncChange.MadeOwner);
        if (callFeature && !couldCall && canCall)
            changes.Add(PairSyncChange.AllowedCall);
        return changes;
    }

    /// <summary>The chat line for <paramref name="change"/> (label: nickname, then Name@World, then key).</summary>
    public static string Describe(PairSyncChange change, string label) => change switch
    {
        PairSyncChange.PartnerAdded => $"Pairing sync added partner {label}.",
        PairSyncChange.MadeOwner => $"Pairing sync made {label} an owner.",
        _ => $"Pairing sync allowed {label} to call you.",
    };

    private static bool IsKey(string? stored, string key) =>
        string.Equals(PairingKeyUtil.Normalize(stored), key, StringComparison.Ordinal);
}
