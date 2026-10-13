namespace WindUpKey;

/// <summary>
/// Redacted forms for <c>dalamud.log</c> lines (04-D10, the 04-D3 style): a pairing key or ContentId as <c>AB******</c>, a
/// Name@World as <c>Ab******@World</c>. Enough to tell lines apart, never the full value. No Dalamud dependency, and
/// in every build (the lines it serves are Release code).
/// </summary>
public static class LogRedact
{
    private const string Stars = "******";

    /// <summary>A key (or ContentId) as <c>AB******</c>: its first two characters and six stars, whatever its length.</summary>
    public static string Key(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return "(none)";
        var trimmed = key.Trim();
        return (trimmed.Length >= 2 ? trimmed[..2] : trimmed) + Stars;
    }

    /// <summary>A Name@World as <c>Ab******@World</c> (the world kept, the name cut to two characters); anything else as a key.</summary>
    public static string Identity(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
            return "(none)";
        var trimmed = identity.Trim();
        var at = trimmed.LastIndexOf('@');
        return at > 0 && at < trimmed.Length - 1
            ? Key(trimmed[..at]) + "@" + trimmed[(at + 1)..].Trim()
            : Key(trimmed);
    }
}
