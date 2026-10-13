using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WindUpKey.Protocol;

namespace WindUpKey.Services;

/// <summary>
/// What the client sends and accepts on the wire under the issue 03 limits: the send cap, the emote list it sends, and
/// the emote names it shows. No Dalamud dependency.
/// </summary>
public static class RelayWireRules
{
    /// <summary>
    /// The UTF-8 bytes to send for <paramref name="envelope"/>, and whether they're within
    /// <see cref="WireLimits.ClientSendBytes"/> (03-D13). A new client never sends anything bigger, so it never
    /// trips the relay's 1009 close; the caller logs and drops it instead.
    /// </summary>
    public static bool TryEncodeForSend(Envelope envelope, out byte[] bytes)
    {
        bytes = Encoding.UTF8.GetBytes(envelope.Serialize());
        return bytes.Length <= WireLimits.ClientSendBytes;
    }

    /// <summary>
    /// The client's <c>register</c>: its pairing key and relay secret. Never the shared token (05-D6): it goes only in the
    /// upgrade's <c>Authorization</c> header (01-D8), and no relay ever read it from the body, so the field is left out.
    /// </summary>
    public static Envelope Register(string pairingKey, string? secret) =>
        Envelope.Create(MessageTypes.Register, new RegisterPayload
        {
            PairingKey = pairingKey,
            Secret = secret,
        });

    /// <summary>
    /// The doll's emotes as sent in <c>ownerSettingsResult</c> (03-D13): at most <see cref="WireLimits.ListEntries"/>,
    /// each name within <see cref="WireLimits.ShortField"/>, so the relay never drops the reply as malformed.
    /// </summary>
    public static List<OwnerEmoteInfo> OutgoingEmotes(IEnumerable<OwnerEmoteInfo> emotes) =>
        emotes
            .Take(WireLimits.ListEntries)
            .Select(e => new OwnerEmoteInfo { Id = e.Id, Name = RemoteText.Sanitize(e.Name, WireLimits.ShortField) })
            .ToList();

    /// <summary>
    /// The emotes a doll offers, as the owner sees them (03-D12): each named from the owner's own full Emote sheet by
    /// its ID (<paramref name="sheetName"/>, in the owner's language, whether or not the owner has it unlocked). Only
    /// an ID the sheet doesn't have (a patch or data mismatch) falls back to the doll's name, sanitised and capped, or
    /// "Emote #id" when nothing is left. At most <see cref="WireLimits.ListEntries"/>, each ID once.
    /// </summary>
    public static List<(ushort Id, string Name)> ResolveOwnerEmotes(
        IEnumerable<OwnerEmoteInfo?>? remote,
        Func<ushort, string?> sheetName)
    {
        var seen = new HashSet<ushort>();
        var list = new List<(ushort Id, string Name)>();
        foreach (var emote in remote ?? [])
        {
            if (list.Count == WireLimits.ListEntries)
                break;
            if (emote is null || !seen.Add(emote.Id))
                continue;

            var name = sheetName(emote.Id);
            if (string.IsNullOrWhiteSpace(name))
                name = RemoteText.Sanitize(emote.Name, WireLimits.ShortField);
            if (string.IsNullOrWhiteSpace(name))
                name = $"Emote #{emote.Id}";
            list.Add((emote.Id, name));
        }

        return list;
    }
}

/// <summary>
/// The relay socket's request setup, shared by the client's connection and the self-test's probes (04 Architecture): the
/// shared token in an <c>Authorization: Bearer</c> header, never the URL (01-D8). Probes also keep the HTTP response
/// details, so a refused upgrade's status (401) can be read. No Dalamud dependency.
/// </summary>
public static class RelaySocketSetup
{
    public static void Configure(ClientWebSocketOptions options, string? token, bool collectHttpResponseDetails = false)
    {
        if (!string.IsNullOrEmpty(token))
            options.SetRequestHeader("Authorization", $"Bearer {token}");
        options.CollectHttpResponseDetails = collectHttpResponseDetails;

        // 05-D3: ping the relay and drop a connection whose pong doesn't come back, so a half-open path is noticed (the
        // 60 s lock-safety rule then applies) instead of the socket staying Open forever. A dead connection is aborted
        // at about interval + timeout; the receive then ends in an OperationCanceledException with the loop's own
        // token not cancelled, which the connect loop treats as an ordinary disconnect.
        options.KeepAliveInterval = WireLimits.KeepAliveInterval;
        options.KeepAliveTimeout = WireLimits.KeepAliveTimeout;
    }
}

/// <summary>What <see cref="RelayInboundPump.NextAsync"/> got: a parsed envelope, or the relay's close.</summary>
public readonly record struct RelayInbound(Envelope? Envelope, WebSocketCloseStatus? CloseStatus, string? CloseDescription);

/// <summary>
/// The client's side of the relay connection's receive loop (03-D10), one per connection. Reads with
/// <see cref="WireLimits.ClientMessageBytes"/>: an oversized message is discarded and the connection kept, logged
/// once per connection, with no chat line. The envelope is parsed here, on the receive worker; the caller applies it
/// on the framework thread. Binary and unparseable messages are skipped. No Dalamud dependency.
/// </summary>
public sealed class RelayInboundPump(Action<int> onFirstOversized)
{
    private readonly BoundedMessageReader _reader = new();
    private bool _reportedOversized;

    /// <summary>The next envelope, or the close. Throws as the socket does when the connection drops.</summary>
    public async Task<RelayInbound> NextAsync(WebSocket socket, CancellationToken ct)
    {
        while (true)
        {
            var message = await _reader.ReadAsync(socket, WireLimits.ClientMessageBytes, ct).ConfigureAwait(false);
            switch (message.Kind)
            {
                case WireMessageKind.Close:
                    return new RelayInbound(null, message.CloseStatus, message.CloseDescription);
                case WireMessageKind.Oversized:
                    if (!_reportedOversized)
                    {
                        _reportedOversized = true;
                        onFirstOversized(WireLimits.ClientMessageBytes);
                    }

                    continue;
                case WireMessageKind.Text when Envelope.TryParse(message.Text!) is { } envelope:
                    return new RelayInbound(envelope, null, null);
                default:
                    continue;
            }
        }
    }
}
