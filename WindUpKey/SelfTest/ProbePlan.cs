#if WINDUP_TESTING || WINDUP_SELFTEST_UNITS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using WindUpKey.Protocol;

namespace WindUpKey.SelfTest;

/// <summary>What the P2 canary said about relay auth (04-D5).</summary>
public enum AuthActivity
{
    /// <summary>Not probed, or the answer was unclear: own-key probes never run.</summary>
    Unknown,

    /// <summary><c>auth_failed</c> for a malformed secret: Bind or Enforce.</summary>
    Active,

    /// <summary>The canary was accepted (pairSync), or treated as a legacy register: Off, or a relay older than 01.</summary>
    Inactive,
}

/// <summary>
/// The gating rules for probes and live-session checks (04-D5, 04-D6). Pure. The default for anything unclear is to
/// skip: an own-key probe runs only when every condition is positively known.
/// </summary>
public static class ProbePlan
{
    /// <summary>The most partners one run sends a presence query to (04-D6).</summary>
    public const int MaxPresencePartners = 10;

    /// <summary>
    /// The most rate-counted messages one run sends on the live session (04-D6, "about 12"): wind, unwind, one presence
    /// query to a random key and at most <see cref="MaxPresencePartners"/> to partners. The relay allows 120 per minute.
    /// Pings and probes on other connections aren't rate-counted.
    /// </summary>
    public const int RateBudget = 3 + MaxPresencePartners;

    /// <summary>
    /// Whether the own-key probes (P3, P3′) may run (04-D5): the canary said auth is active, the live session's register
    /// was accepted, and the probe targets the live session's current host. Never in Off, never on another host (each
    /// has its own accounts), never while disconnected.
    /// </summary>
    public static bool OwnKeyProbesAllowed(AuthActivity canary, bool liveRegisterAccepted, string? probeHost, string? liveHost) =>
        canary == AuthActivity.Active
        && liveRegisterAccepted
        && !string.IsNullOrEmpty(probeHost)
        && string.Equals(probeHost, liveHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>Why <see cref="OwnKeyProbesAllowed"/> said no, for the SKIP line.</summary>
    public static string OwnKeySkipReason(AuthActivity canary, bool liveRegisterAccepted, string? probeHost, string? liveHost) =>
        canary switch
        {
            AuthActivity.Inactive => "relay auth is not active (Off or older than 01): no own-key probe is ever sent",
            AuthActivity.Unknown => "the auth canary gave no clear answer",
            _ when !liveRegisterAccepted => "the live session's register isn't accepted",
            _ => "the probe host isn't the live session's current host",
        };

    /// <summary>
    /// The key the own-key probes address (04-D11): the key the live session registered with, and only when it equals
    /// the ContentId-derived key. Null (SKIP) when either is missing or invalid, or they differ.
    /// </summary>
    public static string? OwnProbeKey(string? registeredKey, string? contentIdKey)
    {
        var registered = WireLimits.NormalizeKey(registeredKey);
        return registered.Length == WireLimits.KeyLength
               && string.Equals(registered, WireLimits.NormalizeKey(contentIdKey), StringComparison.Ordinal)
            ? registered
            : null;
    }

    /// <summary>Why <see cref="OwnProbeKey"/> gave no key, for the SKIP line.</summary>
    public static string OwnProbeKeySkipReason(string? registeredKey) =>
        !WireLimits.IsKey(registeredKey)
            ? "the key the live session registered with isn't known"
            : "the key the live session registered with isn't the ContentId-derived key";

    /// <summary>
    /// Whether the wind/unwind probe may run (04-D6): only after P1 proved the relay is 03 or newer (it closed 4 KiB + 1
    /// before register with 1009). An older relay could forward or queue a wind to the random key.
    /// </summary>
    public static bool WindProbeAllowed(bool relayIs03OrNewer, bool liveRegisterAccepted) => relayIs03OrNewer && liveRegisterAccepted;

    /// <summary>
    /// A random key that is never this character's, a partner's (local or the relay's pairSync), a pending request's, a
    /// queued unpair's or an owned doll's (04-D6). <paramref name="candidate"/> makes keys (random by default); null
    /// when every attempt hit an excluded key.
    /// </summary>
    public static string? PickUnpairedKey(string ownKey, IEnumerable<string?> excluded, Func<string>? candidate = null, int attempts = 16)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal) { WireLimits.NormalizeKey(ownKey) };
        foreach (var key in excluded)
            taken.Add(WireLimits.NormalizeKey(key));

        candidate ??= RandomKey;
        for (var i = 0; i < attempts; i++)
        {
            var key = WireLimits.NormalizeKey(candidate());
            if (key.Length == WireLimits.KeyLength && !taken.Contains(key))
                return key;
        }

        return null;
    }

    /// <summary>A uniformly random key from the pairing-key alphabet.</summary>
    public static string RandomKey()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        Span<char> chars = stackalloc char[WireLimits.KeyLength];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return new string(chars);
    }

    /// <summary>The partners a run queries for presence: at most <see cref="MaxPresencePartners"/>, distinct keys, never this one.</summary>
    public static IReadOnlyList<string> PresencePartners(string ownKey, IEnumerable<string?> partners)
    {
        var own = WireLimits.NormalizeKey(ownKey);
        return partners
            .Select(WireLimits.NormalizeKey)
            .Where(k => k.Length == WireLimits.KeyLength && k != own)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxPresencePartners)
            .ToList();
    }

    /// <summary>The <c>/stats</c> URL of a relay's <c>/ws</c> URL (wss → https, ws → http), or null.</summary>
    public static Uri? StatsUri(string? wsUrl)
    {
        if (string.IsNullOrWhiteSpace(wsUrl) || !Uri.TryCreate(wsUrl.Trim(), UriKind.Absolute, out var ws))
            return null;
        var scheme = ws.Scheme switch
        {
            "wss" => Uri.UriSchemeHttps,
            "ws" => Uri.UriSchemeHttp,
            _ => null,
        };
        if (scheme is null)
            return null;
        var path = ws.AbsolutePath.EndsWith("/ws", StringComparison.Ordinal)
            ? ws.AbsolutePath[..^"/ws".Length] + "/stats"
            : "/stats";
        return new UriBuilder(ws) { Scheme = scheme, Path = path, Query = string.Empty, Port = ws.IsDefaultPort ? -1 : ws.Port }.Uri;
    }
}

/// <summary>Counts the rate-counted messages one run sends on the live session (04-D6). Thread-safe.</summary>
public sealed class SelfTestBudget(int max)
{
    private int _used;

    public int Used => System.Threading.Volatile.Read(ref _used);

    /// <summary>Takes <paramref name="count"/> messages from the budget, or none when that would exceed it.</summary>
    public bool TryTake(int count = 1)
    {
        while (true)
        {
            var used = System.Threading.Volatile.Read(ref _used);
            if (used + count > max)
                return false;
            if (System.Threading.Interlocked.CompareExchange(ref _used, used + count, used) == used)
                return true;
        }
    }
}
#endif
