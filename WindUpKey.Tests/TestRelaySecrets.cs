namespace WindUpKey;

/// <summary>
/// 09-B2: the tests' stand-in for the plugin's RelaySecrets.cs, which is git-excluded and holds the real relay token. That
/// file is never compiled into the tests; the golden files carry this value instead.
/// </summary>
internal static class RelaySecrets
{
    public const string RelayToken = "test-relay-token";
}
