using System;
using System.Security.Cryptography;
using System.Text;

namespace WindUpKey;

public static class PairingKeyUtil
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    public const int Length = 8;

    public static string Generate()
    {
        Span<char> chars = stackalloc char[Length];
        Span<byte> bytes = stackalloc byte[Length];
        RandomNumberGenerator.Fill(bytes);
        for (var i = 0; i < Length; i++)
            chars[i] = Alphabet[bytes[i] % Alphabet.Length];
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
