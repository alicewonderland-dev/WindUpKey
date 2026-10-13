using System;
using System.Collections.Generic;
using System.Linq;

namespace WindUpKey.Services;

/// <summary>
/// The per-character list of unpairs the relay hasn't been told about yet (<see cref="PendingPairRemoval"/>): what is
/// queued, sent, cancelled and kept. No Dalamud dependency.
/// </summary>
public static class PairRemovalQueue
{
    /// <summary>
    /// Remembers an unpair of <paramref name="peerKey"/> until it has been sent, and returns a copy of the entry to send.
    /// Null for an invalid key or the self-key (the debug self-pair is local only). An entry already queued for the key
    /// keeps its owner revoke.
    /// </summary>
    public static PendingPairRemoval? Queue(List<PendingPairRemoval> queue, string peerKey, string localKey, bool revokeOwner)
    {
        var key = PairingKeyUtil.Normalize(peerKey);
        if (!PairingKeyUtil.IsValid(key) || string.Equals(key, PairingKeyUtil.Normalize(localKey), StringComparison.Ordinal))
            return null;

        var entry = queue.FirstOrDefault(r => IsFor(r, key));
        if (entry is null)
        {
            entry = new PendingPairRemoval { PeerKey = key };
            queue.Add(entry);
        }

        entry.RevokeOwner |= revokeOwner;
        return Copy(entry);
    }

    /// <summary>True when an unpair of <paramref name="peerKey"/> is still waiting to be sent.</summary>
    public static bool Contains(IEnumerable<PendingPairRemoval> queue, string peerKey)
    {
        var key = PairingKeyUtil.Normalize(peerKey);
        return queue.Any(r => IsFor(r, key));
    }

    /// <summary>Pairing with <paramref name="peerKey"/> again cancels its queued unpair. True when one was removed.</summary>
    public static bool Cancel(List<PendingPairRemoval> queue, string peerKey)
    {
        var key = PairingKeyUtil.Normalize(peerKey);
        return queue.RemoveAll(r => IsFor(r, key)) > 0;
    }

    /// <summary>
    /// What to send on connect, as copies (the connect runs off the framework thread): valid keys only, never the
    /// self-key.
    /// </summary>
    public static PendingPairRemoval[] ToSend(IEnumerable<PendingPairRemoval> queue, string localKey)
    {
        var self = PairingKeyUtil.Normalize(localKey);
        return Normalize(queue)
            .Where(r => !string.Equals(r.PeerKey, self, StringComparison.Ordinal))
            .ToArray();
    }

    /// <summary>
    /// Drops the entries <paramref name="sent"/> covers, once they were sent. An entry that asks for an owner revoke
    /// the send didn't carry stays queued. True when anything was removed.
    /// </summary>
    public static bool Complete(List<PendingPairRemoval> queue, IEnumerable<PendingPairRemoval> sent)
    {
        var removed = 0;
        foreach (var done in sent)
        {
            var key = PairingKeyUtil.Normalize(done.PeerKey);
            removed += queue.RemoveAll(r => IsFor(r, key) && (!r.RevokeOwner || done.RevokeOwner));
        }

        return removed > 0;
    }

    /// <summary>
    /// A clean copy for the config: valid, normalised keys, one entry per key (keeping an owner revoke if any entry
    /// asked for one), never null.
    /// </summary>
    public static List<PendingPairRemoval> Normalize(IEnumerable<PendingPairRemoval?>? queue) =>
        (queue ?? [])
            .Where(r => r is not null)
            .Select(r => new PendingPairRemoval { PeerKey = PairingKeyUtil.Normalize(r!.PeerKey), RevokeOwner = r.RevokeOwner })
            .Where(r => PairingKeyUtil.IsValid(r.PeerKey))
            .GroupBy(r => r.PeerKey, StringComparer.Ordinal)
            .Select(g => new PendingPairRemoval { PeerKey = g.Key, RevokeOwner = g.Any(r => r.RevokeOwner) })
            .ToList();

    private static bool IsFor(PendingPairRemoval? removal, string key) =>
        removal is not null && string.Equals(PairingKeyUtil.Normalize(removal.PeerKey), key, StringComparison.Ordinal);

    private static PendingPairRemoval Copy(PendingPairRemoval removal) =>
        new() { PeerKey = removal.PeerKey, RevokeOwner = removal.RevokeOwner };
}
