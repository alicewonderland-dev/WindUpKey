using System;
using System.Collections.Generic;
using System.Linq;

namespace WindUpKey;

/// <summary>
/// Compiled-in relay endpoints (Tailscale Funnel).
/// Must stay in sync with WindUpRelay appsettings.Production.json Token.
/// Never log RelayToken.
/// </summary>
public static class RelayDefaults
{
    /// <summary>
    /// Funnel hosts to try, in default order. Only one should be running at a time;
    /// the client fails over when the preferred host is offline.
    /// </summary>
    public static readonly string[] RelayUrls =
    [
        "wss://dollhome-nobara.ancon-universe.ts.net/ws", // Linux
        "wss://dollhome.ancon-universe.ts.net/ws",        // Windows
    ];

    /// <summary>Primary Funnel address (first entry in <see cref="RelayUrls"/>).</summary>
    public static string RelayUrl => RelayUrls[0];

    /// <summary>Shared secret; must match Relay:Token on every host relay.</summary>
    public const string RelayToken = "IJHfCwymh7cG72S0DlFWLvVVqbgXp6XGPOZtygu0Cz4B4eV3LjLAvZzECIu2lJB4";

    /// <summary>
    /// Candidate URLs with an optional sticky preference first (last successful host).
    /// Unknown preferences are ignored so the compiled list stays authoritative.
    /// </summary>
    public static IEnumerable<string> OrderedRelayUrls(string? preferredUrl)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(preferredUrl)
            && RelayUrls.Any(u => string.Equals(u, preferredUrl, StringComparison.OrdinalIgnoreCase)))
        {
            seen.Add(preferredUrl);
            yield return preferredUrl;
        }

        foreach (var url in RelayUrls)
        {
            if (seen.Add(url))
                yield return url;
        }
    }
}
