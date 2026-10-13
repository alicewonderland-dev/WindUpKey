using System.Net.WebSockets;
using System.Text;

namespace WindUpKey.Protocol;

/// <summary>What <see cref="BoundedMessageReader.ReadAsync"/> read.</summary>
public enum WireMessageKind
{
    /// <summary>A whole text message within the limit, decoded.</summary>
    Text,

    /// <summary>
    /// A text message over the limit. Its bytes were discarded up to its end, so the next read is in step; or, with
    /// <c>stopAtLimit</c>, the read stopped as soon as it crossed the limit, mid-message.
    /// </summary>
    Oversized,

    /// <summary>A binary message, discarded up to its end; or, with <c>stopAtLimit</c>, reported at its first frame.</summary>
    Binary,

    /// <summary>The peer's close frame.</summary>
    Close,
}

/// <summary>One message read by <see cref="BoundedMessageReader"/>.</summary>
/// <param name="Kind">What was read.</param>
/// <param name="Text">The decoded text, for <see cref="WireMessageKind.Text"/> only.</param>
/// <param name="CloseStatus">The peer's close status, for <see cref="WireMessageKind.Close"/> only.</param>
/// <param name="CloseDescription">The peer's close description, for <see cref="WireMessageKind.Close"/> only.</param>
public readonly record struct WireMessage(
    WireMessageKind Kind,
    string? Text = null,
    WebSocketCloseStatus? CloseStatus = null,
    string? CloseDescription = null);

/// <summary>
/// Reads whole WebSocket messages with a size limit (03-D1), for the relay's and the client's receive loops. Bytes are
/// collected into one buffer that grows up to the limit and decoded as UTF-8 once at the end, so a character split
/// across frames survives. Past the limit it only discards, up to <c>EndOfMessage</c>, and reports
/// <see cref="WireMessageKind.Oversized"/>; memory never exceeds the limit. One per connection; not thread-safe (a
/// socket has one receive loop).
/// </summary>
/// <param name="stopAtLimit">
/// 03-D16, for the relay, which closes the connection on an oversized or binary message: return
/// <see cref="WireMessageKind.Oversized"/> the moment a message crosses the limit, and
/// <see cref="WireMessageKind.Binary"/> at a binary message's first frame, without reading to <c>EndOfMessage</c>, so a
/// message that never ends can't keep the reader busy. The socket is then mid-message, so the caller must close it. The
/// client keeps the default (03-D10): discard to <c>EndOfMessage</c> and stay in step.
/// </param>
public sealed class BoundedMessageReader(bool stopAtLimit = false)
{
    private const int InitialBytes = 16 * 1024;
    private const int DiscardBytes = 4 * 1024;

    private byte[] _buffer = new byte[InitialBytes];
    private readonly byte[] _discard = new byte[DiscardBytes];

    /// <summary>
    /// Reads the next message of at most <paramref name="maxBytes"/> UTF-8 bytes (all of its frames). Exactly
    /// <paramref name="maxBytes"/> is accepted; one byte more is <see cref="WireMessageKind.Oversized"/>.
    /// </summary>
    public async Task<WireMessage> ReadAsync(WebSocket socket, int maxBytes, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        var count = 0;
        var oversized = false;
        WebSocketMessageType? type = null;
        while (true)
        {
            // Collect while within the limit; once the limit is reached, any further byte makes it oversized.
            var collecting = !oversized && type != WebSocketMessageType.Binary;
            var room = Math.Min(_buffer.Length, maxBytes) - count;
            if (collecting && room == 0 && _buffer.Length < maxBytes)
            {
                Array.Resize(ref _buffer, (int)Math.Min((long)_buffer.Length * 2, maxBytes));
                room = Math.Min(_buffer.Length, maxBytes) - count;
            }

            var intoBuffer = collecting && room > 0;
            var target = intoBuffer ? _buffer.AsMemory(count, room) : _discard.AsMemory();
            var result = await socket.ReceiveAsync(target, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return new WireMessage(WireMessageKind.Close, CloseStatus: socket.CloseStatus, CloseDescription: socket.CloseStatusDescription);

            type ??= result.MessageType;
            if (intoBuffer)
                count += result.Count;
            else if (collecting && result.Count > 0)
                oversized = true;

            if (result.EndOfMessage || (stopAtLimit && (oversized || type == WebSocketMessageType.Binary)))
                break;
        }

        if (type == WebSocketMessageType.Binary)
            return new WireMessage(WireMessageKind.Binary);
        if (oversized)
            return new WireMessage(WireMessageKind.Oversized);
        return new WireMessage(WireMessageKind.Text, Encoding.UTF8.GetString(_buffer, 0, count));
    }
}
