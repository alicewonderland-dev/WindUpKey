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
    /// Funnel hosts to try, in order. There is one relay host, the Oracle Cloud VM, with no standby: see HOSTING.md in
    /// the private WindUpRelay repo for running, updating and rebuilding it. Older plugin builds also list the retired
    /// <c>dollhome</c> host, which stays off so no client's state is split across two databases.
    /// Every entry must be a real Tailscale machine with Funnel enabled — an
    /// unresolvable name here costs each client a failed connection attempt.
    /// Funnel always terminates on public :443, so these URLs carry no port and
    /// are unaffected by the relay's local listen port.
    /// </summary>
    public static readonly string[] RelayUrls =
    [
        "wss://windup-relay-oracle.ancon-universe.ts.net/ws", // Oracle Cloud (the only host)
    ];

    /// <summary>The relay's Funnel address (the first, and now only, entry in <see cref="RelayUrls"/>).</summary>
    public static string RelayUrl => RelayUrls[0];

    /// <summary>
    /// Shared secret; must match the relay's Relay:Token. Lives in the
    /// gitignored RelaySecrets.cs, not here — see RelaySecrets.cs.example.
    /// </summary>
    public const string RelayToken = RelaySecrets.RelayToken;

    /// <summary>
    /// Candidate URLs with an optional sticky preference first (last successful host).
    /// Unknown preferences (such as a saved retired host) are ignored so the compiled list stays authoritative.
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
