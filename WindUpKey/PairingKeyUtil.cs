using System;
using System.Security.Cryptography;
using System.Text;
using WindUpKey.Protocol;

namespace WindUpKey;

public static class PairingKeyUtil
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    public const int Length = 8;

    /// <summary>
    /// App pepper so codes are not a raw Name@World hash (username stays one-way).
    /// </summary>
    private const string Pepper = "WindUpKey/pairing/v1";

    /// <summary>
    /// Stable 8-character key derived from Name@World. Same identity always yields the same key;
    /// the identity cannot be recovered from the key.
    /// </summary>
    public static string FromIdentity(string nameAtWorld)
    {
        var normalized = PlayerIdentity.Normalize(nameAtWorld).ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized))
            return string.Empty;

        var input = Encoding.UTF8.GetBytes(Pepper + "\0" + normalized);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);

        Span<char> chars = stackalloc char[Length];
        for (var i = 0; i < Length; i++)
            chars[i] = Alphabet[hash[i] % Alphabet.Length];
        return new string(chars);
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
