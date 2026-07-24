using System;

namespace WindUpKey;

[Serializable]
public sealed class PairedPartner
{
    /// <summary>
    /// Optional local-only Name@World for context-menu matching. Never sent on the wire.
    /// </summary>
    public string Identity { get; set; } = string.Empty;

    /// <summary>True after Name@World has been explicitly saved, including an empty value.</summary>
    public bool IsIdentitySaved { get; set; }

    /// <summary>Optional local-only display name for this partner.</summary>
    public string Nickname { get; set; } = string.Empty;

    /// <summary>True after the nickname has been explicitly saved, including an empty value.</summary>
    public bool IsNicknameSaved { get; set; }

    /// <summary>Optional local-only title assigned to this owner by the doll.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>True after the title has been explicitly saved, including an empty value.</summary>
    public bool IsTitleSaved { get; set; }

    /// <summary>Partner's 8-character pairing key.</summary>
    public string PartnerKey { get; set; } = string.Empty;

    /// <summary>Doll-side: when true, this partner may wind you.</summary>
    public bool CanWindMe { get; set; }

    /// <summary>Doll-side: when true, this partner may clear your wind (set time to 0).</summary>
    public bool CanUnwindMe { get; set; }

    /// <summary>Doll-side: when true, this partner is an owner (always wind/unwind; remote settings).</summary>
    public bool IsOwner { get; set; }

    /// <summary>Doll-side: when true, this owner may call you to them (Hardcore forces allow).</summary>
    public bool CanCallMe { get; set; }

    /// <summary>Local preferred name, falling back to Name@World and then the pairing key.</summary>
    public string GetChosenName()
    {
        if (!string.IsNullOrWhiteSpace(Nickname))
            return Nickname.Trim();
        if (!string.IsNullOrWhiteSpace(Identity))
            return Identity.Trim();
        return PartnerKey?.Trim() ?? string.Empty;
    }

    /// <summary>Local pair label for messages, with an owner title when one is set.</summary>
    public string GetMessageLabel()
    {
        var name = GetChosenName();
        if (!IsOwner || string.IsNullOrWhiteSpace(Title))
            return name;

        var title = Title.Trim();
        return string.IsNullOrEmpty(name) ? title : $"{title} {name}";
    }
}
