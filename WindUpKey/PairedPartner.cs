using System;

namespace WindUpKey;

[Serializable]
public sealed class PairedPartner
{
    /// <summary>
    /// Optional local-only Name@World for context-menu matching. Never sent on the wire.
    /// </summary>
    public string Identity { get; set; } = string.Empty;

    /// <summary>Partner's 8-character pairing key.</summary>
    public string PartnerKey { get; set; } = string.Empty;

    /// <summary>Doll-side: when true, this partner may wind you.</summary>
    public bool CanWindMe { get; set; }

    /// <summary>Doll-side: when true, this partner may clear your wind (set time to 0).</summary>
    public bool CanUnwindMe { get; set; }

    /// <summary>Doll-side: when true, this partner is an owner (always wind/unwind; remote settings).</summary>
    public bool IsOwner { get; set; }
}
