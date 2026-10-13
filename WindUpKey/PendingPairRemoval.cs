using System;

namespace WindUpKey;

/// <summary>
/// An unpair the relay hasn't been told about yet (made while offline, or its send failed). Sent on the next connect,
/// then dropped. Stored per character in <see cref="CharacterProfile.PendingPairRemovals"/>.
/// </summary>
[Serializable]
public sealed class PendingPairRemoval
{
    /// <summary>The former partner's 8-character pairing key.</summary>
    public string PeerKey { get; set; } = string.Empty;

    /// <summary>The partner was this character's owner, so an <c>ownerRevoked</c> goes before the <c>pairRemove</c>.</summary>
    public bool RevokeOwner { get; set; }
}
