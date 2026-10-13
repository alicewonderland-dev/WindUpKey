using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using WindUpKey.Protocol;
using WindUpKey.SelfTest;
using WindUpKey.Services;

namespace WindUpKey.Tests;

/// <summary>
/// The client's side of issue 05: keep-alive on the relay socket (05-D3), no token in the register body (05-D6), and the
/// first-bind limit's close never being an auth signal (05-D2) but explained in the status (05-D9).
/// </summary>
public sealed class RelayHardeningClientTests
{
    [Fact]
    public void TheRelaySocket_PingsEvery30s_AndDropsAConnectionWhosePongIsMissingFor90s()
    {
        using var socket = new ClientWebSocket();

        RelaySocketSetup.Configure(socket.Options, "token");

        Assert.Equal(TimeSpan.FromSeconds(30), socket.Options.KeepAliveInterval);
        Assert.Equal(TimeSpan.FromSeconds(90), socket.Options.KeepAliveTimeout);
    }

    [Fact]
    public async Task TheProbeSockets_GetTheSameKeepAlive()
    {
        // The self-test's real connector (RelayProbeConnectors) against a local WebSocket server: the socket it hands back
        // is set up like the client's own.
        using var listener = StartLocalListener(out var port);
        var serve = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            var accepted = await context.AcceptWebSocketAsync(null);
            var buffer = new byte[16];
            try
            {
                await accepted.WebSocket.ReceiveAsync(buffer, CancellationToken.None);
            }
            catch (Exception ex) when (ex is WebSocketException or HttpListenerException or IOException)
            {
                // The probe socket was disposed.
            }
        });

        var connection = await new ClientWebSocketProbeConnector("token")
            .ConnectAsync(new Uri($"ws://localhost:{port}/ws"), withToken: true, TimeSpan.FromSeconds(5), CancellationToken.None);

        var socket = Assert.IsType<ClientWebSocket>(connection.Socket);
        using (socket)
        {
            Assert.Equal(101, connection.HttpStatus);
            Assert.Equal(WireLimits.KeepAliveInterval, socket.Options.KeepAliveInterval);
            Assert.Equal(WireLimits.KeepAliveTimeout, socket.Options.KeepAliveTimeout);
        }

        await serve.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// 05-D13: an <see cref="HttpListener"/> on a free loopback port. The <c>localhost</c> prefix needs no admin rights
    /// (no URL reservation) on Windows either. HttpListener can't take port 0, so a free port is picked first, and a
    /// pick that another process takes before the listener starts is retried with a fresh one.
    /// </summary>
    private static HttpListener StartLocalListener(out int port)
    {
        for (var attempt = 1; ; attempt++)
        {
            using (var probe = new TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
            }

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}/");
            try
            {
                listener.Start();
                return listener;
            }
            catch (HttpListenerException) when (attempt < 5)
            {
                // Taken in the meantime: try another.
                listener.Close();
            }
        }
    }

    [Fact]
    public void TheRegister_CarriesKeyAndSecret_ButNoToken()
    {
        var json = RelayWireRules.Register("KEYAAAA1", "secret-value").Serialize();

        var payload = JsonNode.Parse(json)!["payload"]!.AsObject();
        Assert.False(payload.ContainsKey("token"), json);
        Assert.Equal("KEYAAAA1", (string?)payload["pairingKey"]);
        Assert.Equal("secret-value", (string?)payload["secret"]);
        Assert.Equal(MessageTypes.Register, (string?)JsonNode.Parse(json)!["type"]);
    }

    [Theory]
    [InlineData((WebSocketCloseStatus)1013)]
    [InlineData(WebSocketCloseStatus.PolicyViolation)]
    public void TheFirstBindLimitClose_IsNeverAnAuthSignal(WebSocketCloseStatus status)
    {
        // The client reconnects on its normal backoff, and the 60 s lock-safety rule applies meanwhile.
        Assert.Equal(RelaySessionSignal.None, RelayConnectPolicy.ClassifyClose(status, WireCloseReasons.BindRateLimited));
    }

    [Fact]
    public void TheFirstBindLimitClose_ExplainsItselfInTheStatus()
    {
        // 05-D9: the config window says why, while the normal backoff retries.
        Assert.Equal(
            "The relay is limiting new accounts from your network right now; retrying automatically.",
            RelayConnectPolicy.CloseStatus((WebSocketCloseStatus)1013, WireCloseReasons.BindRateLimited));
    }

    [Theory]
    [InlineData(WebSocketCloseStatus.PolicyViolation, "bind_rate_limited")]
    [InlineData((WebSocketCloseStatus)1013, "something_else")]
    [InlineData((WebSocketCloseStatus)1013, null)]
    [InlineData(WebSocketCloseStatus.NormalClosure, "bye")]
    [InlineData(WebSocketCloseStatus.PolicyViolation, "auth_failed")]
    [InlineData(WebSocketCloseStatus.PolicyViolation, "session_replaced")]
    [InlineData(WebSocketCloseStatus.MessageTooBig, "message_too_big")]
    [InlineData(null, null)]
    public void OtherCloses_ExplainNothing(WebSocketCloseStatus? status, string? description)
    {
        Assert.Null(RelayConnectPolicy.CloseStatus(status, description));
    }

    // ---- 05-D13: the status never carries into a new connect cycle ----

    [Fact]
    public void TheBindLimitStatus_IsShownForItsOwnCycle_UntilAnotherCloseOrAClear()
    {
        var policy = new RelayConnectPolicy();
        var epoch = policy.Epoch;

        policy.RecordClose(epoch, (WebSocketCloseStatus)1013, WireCloseReasons.BindRateLimited);
        Assert.Equal(RelayConnectPolicy.BindRateLimitedStatus, policy.CurrentCloseStatus);

        policy.RecordClose(epoch, WebSocketCloseStatus.NormalClosure, "bye");
        Assert.Null(policy.CurrentCloseStatus);

        policy.RecordClose(epoch, (WebSocketCloseStatus)1013, WireCloseReasons.BindRateLimited);
        policy.ClearCloseStatus();
        Assert.Null(policy.CurrentCloseStatus);
    }

    [Fact]
    public void ANewCycle_ClearsTheBindLimitStatus()
    {
        // A character switch (or login, Reconnect, import, revert) resets the cycle.
        var policy = new RelayConnectPolicy();
        policy.RecordClose(policy.Epoch, (WebSocketCloseStatus)1013, WireCloseReasons.BindRateLimited);

        policy.Reset();

        Assert.Null(policy.CurrentCloseStatus);
    }

    [Fact]
    public void ABindLimitCloseFromAnEarlierCycle_NeverCarriesIntoTheNewOne()
    {
        // The old connection's close is read after the reset: it explains nothing about the new cycle.
        var policy = new RelayConnectPolicy();
        var old = policy.Epoch;
        policy.Reset();

        policy.RecordClose(old, (WebSocketCloseStatus)1013, WireCloseReasons.BindRateLimited);

        Assert.Null(policy.CurrentCloseStatus);
        policy.RecordClose(policy.Epoch, (WebSocketCloseStatus)1013, WireCloseReasons.BindRateLimited);
        Assert.Equal(RelayConnectPolicy.BindRateLimitedStatus, policy.CurrentCloseStatus);
    }
}
