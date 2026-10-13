using System.Net.WebSockets;
using System.Text;
using WindUpKey.Protocol;
using WindUpKey.Services;

namespace WindUpKey.Tests;

/// <summary>
/// The client's side of issue 03: the receive limit that keeps the connection (03-D10), remote text (03-D11), emote
/// names on the owner's side (03-D12), the send cap (03-D13), and the reconnect backoff and close classification
/// (03-D14).
/// </summary>
public sealed class RelayWireRulesTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    // ---------- 03-D10: the receive pump ----------

    private static Task SendTextAsync(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    private static string Ping(string id) => Envelope.Create(MessageTypes.Ping, new { id }).Serialize();

    [Fact]
    public async Task AnOversizedMessage_IsDiscarded_TheConnectionKept_AndTheNextOneDelivered()
    {
        var (server, client) = InMemorySockets.CreatePair();
        using var _ = server;
        using var __ = client;
        var reports = new List<int>();
        var pump = new RelayInboundPump(reports.Add);

        var oversized = Ping(new string('x', WireLimits.ClientMessageBytes));
        var send = Task.Run(async () =>
        {
            await SendTextAsync(server, oversized);
            await SendTextAsync(server, Ping("next"));
            await SendTextAsync(server, oversized);
            await SendTextAsync(server, Ping("last"));
        });

        var next = await pump.NextAsync(client, CancellationToken.None).WaitAsync(Wait);
        Assert.Equal("next", (string?)next.Envelope!.Payload!["id"]);
        Assert.Equal(WebSocketState.Open, client.State);
        var last = await pump.NextAsync(client, CancellationToken.None).WaitAsync(Wait);
        Assert.Equal("last", (string?)last.Envelope!.Payload!["id"]);
        Assert.Equal(WebSocketState.Open, client.State);
        await send;

        // Logged once per connection, not per message.
        Assert.Equal([WireLimits.ClientMessageBytes], reports);
    }

    [Fact]
    public async Task AnOversizedMessage_IsDiscardedToItsEnd_SoItsTailIsNeverReadAsAMessage()
    {
        // 03-D10/03-D16: the client keeps discard-to-end (only the relay stops at the limit). Past the limit this message
        // is whitespace and then a valid envelope, so any part of it read as a message of its own would be delivered.
        var (server, client) = InMemorySockets.CreatePair();
        using var _ = server;
        using var __ = client;
        var pump = new RelayInboundPump(_ => { });
        var smuggling = new string('x', WireLimits.ClientMessageBytes + 1) + new string(' ', 16 * 1024) + Ping("tail");
        var send = Task.Run(async () =>
        {
            await SendTextAsync(server, smuggling);
            await SendTextAsync(server, Ping("next"));
        });

        var next = await pump.NextAsync(client, CancellationToken.None).WaitAsync(Wait);

        Assert.Equal("next", (string?)next.Envelope!.Payload!["id"]);
        await send;
    }

    [Fact]
    public async Task AMessageOfExactlyTheClientLimit_IsDelivered()
    {
        var (server, client) = InMemorySockets.CreatePair();
        using var _ = server;
        using var __ = client;
        var prefix = Ping("big")[..^1] + ",\"pad\":\"";
        var exact = prefix + new string('x', WireLimits.ClientMessageBytes - prefix.Length - 2) + "\"}";
        Assert.Equal(WireLimits.ClientMessageBytes, Encoding.UTF8.GetByteCount(exact));
        var send = SendTextAsync(server, exact);

        var inbound = await new RelayInboundPump(_ => Assert.Fail("not oversized")).NextAsync(client, CancellationToken.None).WaitAsync(Wait);

        Assert.Equal("big", (string?)inbound.Envelope!.Payload!["id"]);
        await send;
    }

    [Fact]
    public async Task BinaryAndUnparseableMessages_AreSkipped_AndACloseIsReturned()
    {
        var (server, client) = InMemorySockets.CreatePair();
        using var _ = server;
        using var __ = client;
        var send = Task.Run(async () =>
        {
            await server.SendAsync(new byte[10], WebSocketMessageType.Binary, true, CancellationToken.None);
            await SendTextAsync(server, "not json");
            await SendTextAsync(server, Ping("ok"));
            await server.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, WireCloseReasons.MessageTooBig, CancellationToken.None);
        });
        var pump = new RelayInboundPump(_ => { });

        Assert.Equal("ok", (string?)(await pump.NextAsync(client, CancellationToken.None).WaitAsync(Wait)).Envelope!.Payload!["id"]);
        var closed = await pump.NextAsync(client, CancellationToken.None).WaitAsync(Wait);

        Assert.Null(closed.Envelope);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, closed.CloseStatus);
        Assert.Equal(WireCloseReasons.MessageTooBig, closed.CloseDescription);
        await send;
    }

    // ---------- 03-D11: remote text ----------

    [Fact]
    public void Sanitize_RemovesControlCharacters_AndTrims()
    {
        Assert.Equal("Name@World", RemoteText.Sanitize("  Na\u0002me\u0003@Wor\nld\u0000\t ", 64));
        Assert.Equal("a b", RemoteText.Sanitize("\u001ba b\u007f\u0085", 64));
        Assert.Equal(string.Empty, RemoteText.Sanitize(null, 64));
        Assert.Equal(string.Empty, RemoteText.Sanitize("\u0002\u0003 ", 64));
    }

    [Fact]
    public void Sanitize_CapsTheLength_WithoutSplittingASurrogatePair()
    {
        Assert.Equal(new string('a', 64), RemoteText.Sanitize(new string('a', 1000), 64));
        Assert.Equal("abc", RemoteText.Sanitize("abc", 3));

        // Cutting at 4 would leave half of the emoji.
        Assert.Equal("abc", RemoteText.Sanitize("abc\U0001F600def", 4));
        Assert.Equal("abc\U0001F600", RemoteText.Sanitize("abc\U0001F600def", 5));
        Assert.Equal("ab", RemoteText.Sanitize("ab   cdef", 4));
    }

    [Fact]
    public void WholeOrEmpty_IgnoresAnOverLongValue_InsteadOfTruncatingIt()
    {
        Assert.Equal("Twintania", RemoteText.WholeOrEmpty(" Twintania ", WireLimits.ShortField));
        Assert.Equal(new string('w', 64), RemoteText.WholeOrEmpty(new string('w', 64), WireLimits.ShortField));
        Assert.Equal(string.Empty, RemoteText.WholeOrEmpty(new string('w', 65), WireLimits.ShortField));
        Assert.Equal(string.Empty, RemoteText.WholeOrEmpty(null, WireLimits.ShortField));
    }

    // ---------- 03-D12: emote names on the owner's side ----------

    private static readonly Dictionary<ushort, string> Sheet = new() { [50] = "Sitzen", [52] = "Am Boden sitzen", [88] = "Tanzen" };

    private static string? SheetName(ushort id) => Sheet.GetValueOrDefault(id);

    [Fact]
    public void OwnerEmotes_AreNamedFromTheOwnersSheetByID_NotByTheDollsName()
    {
        var resolved = RelayWireRules.ResolveOwnerEmotes(
        [
            new OwnerEmoteInfo { Id = 52, Name = "Sit on Ground" },
            new OwnerEmoteInfo { Id = 88, Name = "\u0002evil\u0003" },
        ], SheetName);

        Assert.Equal([((ushort)52, "Am Boden sitzen"), ((ushort)88, "Tanzen")], resolved);
    }

    [Fact]
    public void OwnerEmotes_MissingFromTheSheet_FallBackToTheDollsNameSanitisedAndCapped()
    {
        var resolved = RelayWireRules.ResolveOwnerEmotes(
        [
            new OwnerEmoteInfo { Id = 9001, Name = " New\u0002 emote " + new string('x', 200) },
            new OwnerEmoteInfo { Id = 9002, Name = "\u0002" },
            null,
        ], SheetName);

        Assert.Equal(2, resolved.Count);
        Assert.Equal(9001, resolved[0].Id);
        Assert.StartsWith("New emote x", resolved[0].Name);
        Assert.Equal(WireLimits.ShortField, resolved[0].Name.Length);
        Assert.Equal(((ushort)9002, "Emote #9002"), resolved[1]);
    }

    [Fact]
    public void OwnerEmotes_AreCappedAt512_EachIdOnce()
    {
        var remote = Enumerable.Range(0, 2000).Select(i => new OwnerEmoteInfo { Id = (ushort)(i % 700), Name = "e" + i });

        var resolved = RelayWireRules.ResolveOwnerEmotes(remote, SheetName);

        Assert.Equal(WireLimits.ListEntries, resolved.Count);
        Assert.Equal(resolved.Count, resolved.Select(e => e.Id).Distinct().Count());
        Assert.Empty(RelayWireRules.ResolveOwnerEmotes(null, SheetName));

        // A repeated ID is listed once (its ImGui ID is the emote ID).
        var repeated = RelayWireRules.ResolveOwnerEmotes(
            [new OwnerEmoteInfo { Id = 52, Name = "a" }, new OwnerEmoteInfo { Id = 52, Name = "b" }, new OwnerEmoteInfo { Id = 88, Name = "c" }],
            SheetName);
        Assert.Equal([(ushort)52, (ushort)88], repeated.Select(e => e.Id));
    }

    // ---------- 03-D13: what the client sends ----------

    [Fact]
    public void Sends_UpToTheRelaysLimit_AreEncoded_BiggerOnesAreRefused()
    {
        static Envelope Padded(int bytes)
        {
            var empty = Envelope.Create(MessageTypes.Ping, new { pad = string.Empty }).Serialize();
            return Envelope.Create(MessageTypes.Ping, new { pad = new string('x', bytes - Encoding.UTF8.GetByteCount(empty)) });
        }

        Assert.True(RelayWireRules.TryEncodeForSend(Padded(WireLimits.ClientSendBytes), out var exact));
        Assert.Equal(WireLimits.ClientSendBytes, exact.Length);
        Assert.False(RelayWireRules.TryEncodeForSend(Padded(WireLimits.ClientSendBytes + 1), out var over));
        Assert.Equal(WireLimits.ClientSendBytes + 1, over.Length);
        Assert.Equal(WireLimits.RelayMessageBytes, WireLimits.ClientSendBytes);
    }

    [Fact]
    public void OutgoingEmotes_AreTrimmedTo512_WithNamesWithinTheFieldLimit()
    {
        var emotes = Enumerable.Range(0, 600).Select(i => new OwnerEmoteInfo { Id = (ushort)i, Name = i == 0 ? new string('n', 100) : "e" + i });

        var sent = RelayWireRules.OutgoingEmotes(emotes);

        Assert.Equal(WireLimits.ListEntries, sent.Count);
        Assert.Equal(WireLimits.ShortField, sent[0].Name.Length);
        Assert.Equal("e511", sent[^1].Name);

        // And the relay accepts the reply that carries them.
        var reply = new OwnerSettingsResultPayload { RequestId = Guid.NewGuid().ToString("N"), From = "KEYAAAA1", To = "PEERBBB2", Emotes = sent };
        Assert.True(WireLimits.IsValid(reply));
    }

    // ---------- 03-D14: backoff and close classification ----------

    [Fact]
    public void TheBackoff_GrowsAcrossConnectionsThatAreNeverAccepted_UpToTheMaximum()
    {
        var backoff = new RelayReconnectBackoff();
        var delays = Enumerable.Range(0, 8).Select(_ => backoff.Next().TotalSeconds).ToArray();

        Assert.Equal([1, 2, 4, 8, 16, 30, 30, 30], delays);
    }

    [Fact]
    public void TheBackoff_ResetsOnlyWhenARegisterIsAccepted_OrOnReset()
    {
        var backoff = new RelayReconnectBackoff();
        for (var i = 0; i < 4; i++)
            backoff.Next();
        Assert.Equal(TimeSpan.FromSeconds(16), backoff.Current);

        backoff.RegisterAccepted();
        Assert.Equal(RelayReconnectBackoff.Initial, backoff.Next());
        Assert.Equal(TimeSpan.FromSeconds(2), backoff.Next());

        backoff.Reset();
        Assert.Equal(RelayReconnectBackoff.Initial, backoff.Current);
    }

    [Theory]
    [InlineData(WebSocketCloseStatus.MessageTooBig, WireCloseReasons.MessageTooBig)]
    [InlineData(WebSocketCloseStatus.InvalidMessageType, WireCloseReasons.BinaryNotSupported)]
    [InlineData(WebSocketCloseStatus.PolicyViolation, WireCloseReasons.RegisterTimeout)]
    [InlineData(WebSocketCloseStatus.PolicyViolation, WireCloseReasons.RegisterRequired)]
    public void TheRelaysLimitCloses_AreNeverAnAuthSignal(WebSocketCloseStatus status, string description)
    {
        Assert.Equal(RelaySessionSignal.None, RelayConnectPolicy.ClassifyClose(status, description));
    }

    [Fact]
    public void QueueFull_IsAnOrdinaryError_NotRelayOnly()
    {
        Assert.False(ErrorCodes.IsRelayOnly(ErrorCodes.QueueFull));
        Assert.Equal(RelaySessionSignal.None, RelayConnectPolicy.ClassifyError(ErrorCodes.QueueFull, "KEYAAAA1"));
        Assert.Equal(RelaySessionSignal.None, RelayConnectPolicy.ClassifyError(ErrorCodes.QueueFull, null));
    }
}
