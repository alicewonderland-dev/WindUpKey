namespace WindUpKey;

/// <summary>
/// Compiled-in relay endpoint (Tailscale Funnel).
/// Must stay in sync with WindUpRelay appsettings.Production.json Token.
/// Never log RelayToken.
/// </summary>
public static class RelayDefaults
{
    /// <summary>Same-PC host (WindUpRelay.Host). Tried first.</summary>
    public const string LocalRelayUrl = "ws://127.0.0.1:8787/ws";

    /// <summary>Stable Funnel address for machine nickname dollhome.</summary>
    public const string RelayUrl = "wss://dollhome.ancon-universe.ts.net/ws";

    /// <summary>Shared secret; must match Relay:Token on the host relay.</summary>
    public const string RelayToken = "IJHfCwymh7cG72S0DlFWLvVVqbgXp6XGPOZtygu0Cz4B4eV3LjLAvZzECIu2lJB4";
}
