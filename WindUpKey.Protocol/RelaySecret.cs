using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace WindUpKey.Protocol;

/// <summary>
/// Per-character relay secret: 32 random bytes as unpadded base64url (43 chars).
/// Sent in <see cref="RegisterPayload.Secret"/>; the relay stores only <see cref="HashHex"/>.
/// Never log a secret or its hash.
/// </summary>
public static class RelaySecret
{
    public const int ByteLength = 32;

    /// <summary>Unpadded base64url length of <see cref="ByteLength"/> bytes.</summary>
    public const int EncodedLength = 43;

    public static string Generate() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(ByteLength));

    /// <summary>
    /// Canonical form of a pasted secret: whitespace and trailing '=' padding removed,
    /// standard base64 '+' and '/' mapped to base64url. Returns empty for null/blank input.
    /// Does not validate; see <see cref="IsValid"/>.
    /// </summary>
    public static string Normalize(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
            return string.Empty;

        var sb = new StringBuilder(EncodedLength);
        foreach (var c in secret)
        {
            if (char.IsWhiteSpace(c))
                continue;
            sb.Append(c switch
            {
                '+' => '-',
                '/' => '_',
                _ => c,
            });
        }

        return sb.ToString().TrimEnd('=');
    }

    /// <summary>True when <paramref name="secret"/> is already in canonical form and decodes to 32 bytes.</summary>
    public static bool IsValid(string? secret) => TryDecode(secret, out _);

    /// <summary>
    /// Decodes a canonical secret: exactly 43 base64url characters. The decoder also rejects
    /// non-zero trailing bits, so every 32-byte value has one accepted spelling.
    /// </summary>
    public static bool TryDecode(string? secret, out byte[] bytes)
    {
        bytes = [];
        if (secret is null || secret.Length != EncodedLength)
            return false;

        foreach (var c in secret)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
                return false;
        }

        byte[] decoded;
        try
        {
            decoded = Base64Url.DecodeFromChars(secret);
        }
        catch (FormatException)
        {
            return false;
        }

        if (decoded.Length != ByteLength)
            return false;

        bytes = decoded;
        return true;
    }

    /// <summary>Lowercase hex SHA-256 of the decoded secret bytes, or null when the secret is invalid.</summary>
    public static string? HashHex(string? secret)
    {
        if (!TryDecode(secret, out var bytes))
            return null;
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>Constant-time comparison of two stored/computed hex hashes (case-insensitive hex).</summary>
    public static bool HashEquals(string? a, string? b)
    {
        if (a is null || b is null)
            return false;
        var left = Encoding.ASCII.GetBytes(a.ToLowerInvariant());
        var right = Encoding.ASCII.GetBytes(b.ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(left, right);
    }
}
