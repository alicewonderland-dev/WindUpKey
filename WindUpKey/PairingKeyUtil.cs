using System;
using System.Security.Cryptography;
using System.Text;

namespace WindUpKey;

public static class PairingKeyUtil
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    public const int Length = 8;

    /// <summary>
    /// App pepper so codes are not a raw ContentId hash (id stays one-way).
    /// v2: ContentId seed (v1 was Name@World).
    /// </summary>
    private const string Pepper = "WindUpKey/pairing/v2";

    /// <summary>
    /// One-way 8-character key derived from ContentId (id cannot be recovered).
    /// Same character always yields the same key across rename, world transfer, and config wipe.
    /// </summary>
    public static string FromContentId(ulong contentId)
    {
        if (contentId == 0)
            return string.Empty;

        var hex = contentId.ToString("X16");
        var input = Encoding.UTF8.GetBytes(Pepper + "\0" + hex);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);

        Span<char> chars = stackalloc char[Length];
        for (var i = 0; i < Length; i++)
            chars[i] = Alphabet[hash[i] % Alphabet.Length];
        return new string(chars);
    }

    /// <summary>Parses a ContentId hex string (e.g. profile key) and derives the pairing key.</summary>
    public static string FromContentIdHex(string? contentIdHex)
    {
        if (string.IsNullOrWhiteSpace(contentIdHex))
            return string.Empty;
        if (!ulong.TryParse(contentIdHex.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var contentId))
            return string.Empty;
        return FromContentId(contentId);
    }

    public static string Normalize(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return string.Empty;
        var sb = new StringBuilder(Length);
        foreach (var c in key.Trim().ToUpperInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
                sb.Append(c);
        }

        return sb.ToString();
    }

    public static bool IsValid(string? key)
    {
        var n = Normalize(key);
        if (n.Length != Length)
            return false;
        foreach (var c in n)
        {
            if (!char.IsAsciiLetterUpper(c) && !char.IsAsciiDigit(c))
                return false;
        }

        return true;
    }
}
