using System;

namespace WindUpKey;

/// <summary>A doll this character has been granted ownership of (owner-side bookkeeping).</summary>
[Serializable]
public sealed class OwnedDoll
{
    /// <summary>Doll's 8-character pairing key.</summary>
    public string DollKey { get; set; } = string.Empty;

    /// <summary>Optional Name@World label from grant / key rotation (local only).</summary>
    public string Identity { get; set; } = string.Empty;
}
