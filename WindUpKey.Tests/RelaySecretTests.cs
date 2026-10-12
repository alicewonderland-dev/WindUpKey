using System.Buffers.Text;
using System.Security.Cryptography;
using WindUpKey.Protocol;

namespace WindUpKey.Tests;

/// <summary><see cref="RelaySecret"/>: the format the plugin generates, accepts on import, and stores.</summary>
public sealed class RelaySecretTests
{
    [Fact]
    public void Generate_Makes43CharacterUnpaddedBase64Url_Of32RandomBytes()
    {
        var generated = Enumerable.Range(0, 20).Select(_ => RelaySecret.Generate()).ToList();

        Assert.Equal(20, generated.Distinct(StringComparer.Ordinal).Count());
        foreach (var secret in generated)
        {
            Assert.Equal(43, secret.Length);
            Assert.DoesNotContain('=', secret);
            Assert.True(RelaySecret.TryDecode(secret, out var bytes));
            Assert.Equal(32, bytes.Length);
            Assert.Equal(secret, Base64Url.EncodeToString(bytes));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB")] // 42 characters
    [InlineData("BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB")] // 44 characters
    [InlineData("BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB=")] // padding
    [InlineData("BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB+")] // standard alphabet
    [InlineData("BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB.")]
    [InlineData("BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB")] // trailing bits not zero
    [InlineData("BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB ")]
    public void IsValid_AcceptsOnlyTheCanonicalSpelling(string? secret)
    {
        Assert.False(RelaySecret.IsValid(secret));
        Assert.Null(RelaySecret.HashHex(secret));
    }

    [Fact]
    public void Normalize_UndoesWhatPastingDoes()
    {
        var bytes = new byte[32];
        bytes[0] = 0xFB; // '+' and '/' in the standard alphabet
        bytes[1] = 0xEF;
        bytes[2] = 0xFF;
        var canonical = Base64Url.EncodeToString(bytes);
        var standard = Convert.ToBase64String(bytes);
        Assert.Contains('+', standard);
        Assert.Contains('/', standard);
        Assert.EndsWith("=", standard);

        Assert.Equal(canonical, RelaySecret.Normalize(" \t" + standard[..10] + "\r\n" + standard[10..] + " "));
        Assert.Equal(canonical, RelaySecret.Normalize(canonical));
        Assert.Equal(string.Empty, RelaySecret.Normalize("  \n"));
        Assert.Equal(string.Empty, RelaySecret.Normalize(null));
    }

    [Fact]
    public void HashHex_IsLowercaseSha256OfTheDecodedBytes()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var secret = Base64Url.EncodeToString(bytes);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), RelaySecret.HashHex(secret));
        Assert.Equal(64, RelaySecret.HashHex(secret)!.Length);
    }

    [Fact]
    public void HashEquals_ComparesHexIgnoringCase_AndRejectsNull()
    {
        var hash = RelaySecret.HashHex(RelaySecret.Generate())!;

        Assert.True(RelaySecret.HashEquals(hash, hash.ToUpperInvariant()));
        Assert.False(RelaySecret.HashEquals(hash, RelaySecret.HashHex(RelaySecret.Generate())));
        Assert.False(RelaySecret.HashEquals(hash, null));
        Assert.False(RelaySecret.HashEquals(null, null));
    }
}
