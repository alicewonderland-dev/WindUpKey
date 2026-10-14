namespace WindUpKey.Tests;

/// <summary>10 (D2, D3): the plugin compiles in one relay host, the Oracle VM, and no longer the retired dollhome.</summary>
public sealed class RelayDefaultsTests
{
    private const string OracleUrl = "wss://windup-relay-oracle.ancon-universe.ts.net/ws";
    private const string RetiredDollhomeUrl = "wss://dollhome.ancon-universe.ts.net/ws";

    [Fact]
    public void RelayUrls_IsExactlyTheOracleUrl()
    {
        Assert.Equal(new[] { OracleUrl }, RelayDefaults.RelayUrls);
        Assert.Equal(OracleUrl, RelayDefaults.RelayUrl);
    }

    [Fact]
    public void NoCompiledUrl_NamesDollhome()
    {
        Assert.DoesNotContain(RelayDefaults.RelayUrls, u => u.Contains("dollhome", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("dollhome", RelayDefaults.RelayUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ASavedRetiredPreference_IsIgnored()
    {
        // A profile whose last successful host was dollhome (an older build) still connects only to the Oracle relay.
        Assert.Equal(new[] { OracleUrl }, RelayDefaults.OrderedRelayUrls(RetiredDollhomeUrl));
        Assert.Equal(new[] { OracleUrl }, RelayDefaults.OrderedRelayUrls(null));
    }
}
