using System.Net.WebSockets;
using System.Text;
using WindUpKey.Protocol;
using WindUpKey.SelfTest;
using WindUpKey.Services;

namespace WindUpKey.Tests;

/// <summary>
/// A fact that runs only against a live relay (04 Tests, "Live relay"): skipped unless
/// <c>WINDUP_SELFTEST_RELAY_URL</c> is set. The issue 04 smoke script starts the relay locally in Off, Bind and
/// Enforce and runs these with <c>--filter Category=LiveRelay</c>.
/// </summary>
public sealed class LiveRelayFactAttribute : FactAttribute
{
    public LiveRelayFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SelfTestLiveRelayTests.UrlVariable)))
            Skip = $"Needs a live relay: set {SelfTestLiveRelayTests.UrlVariable} (the issue 04 smoke script does).";
    }
}

/// <summary>
/// The real probe protocol (<see cref="ClientWebSocketProbeConnector"/>, <see cref="HttpStatsClient"/>) and live-session
/// checks against a locally started relay, with a scripted live session that registered key K with secret S. The
/// smoke script checks the relay's side afterwards: <c>/stats accounts</c> and the outbox unchanged.
/// </summary>
[Trait("Category", "LiveRelay")]
public sealed class SelfTestLiveRelayTests
{
    public const string UrlVariable = "WINDUP_SELFTEST_RELAY_URL";

    private static string Env(string name, string fallback = "") =>
        Environment.GetEnvironmentVariable("WINDUP_SELFTEST_" + name) is { Length: > 0 } value ? value : fallback;

    /// <summary>A live session over a real <see cref="ClientWebSocket"/>, feeding the tap like the client's receive loop.</summary>
    private sealed class LiveHost(string url, string token, string key, string secret, string? partner) : ISelfTestHost, ISelfTestLink, IAsyncDisposable
    {
        private readonly SemaphoreSlim _send = new(1, 1);
        private readonly TaskCompletionSource _accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ClientWebSocket? _socket;
        private Task? _receive;
        private volatile SelfTestTap? _tap;
        private volatile IReadOnlyList<string>? _peers;
        private int _acceptedCount;

        public int SessionReplacedSeen;
        public WebSocketCloseStatus? ClosedWith;
        public int UnconsumedSelfTestReplies;

        public bool Open => _socket?.State == WebSocketState.Open;

        public ISelfTestLink Link => this;

        public async Task StartAsync()
        {
            _socket = new ClientWebSocket();
            RelaySocketSetup.Configure(_socket.Options, token);
            await _socket.ConnectAsync(new Uri(url), CancellationToken.None);
            _receive = Task.Run(ReceiveAsync);
            await SendAsync(Envelope.Create(MessageTypes.Register, new RegisterPayload { PairingKey = key, Secret = secret }), CancellationToken.None);
            await _accepted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private async Task ReceiveAsync()
        {
            var pump = new RelayInboundPump(_ => { });
            try
            {
                while (_socket!.State == WebSocketState.Open)
                {
                    var inbound = await pump.NextAsync(_socket, CancellationToken.None);
                    if (inbound.Envelope is not { } envelope)
                    {
                        ClosedWith = inbound.CloseStatus;
                        if (RelayConnectPolicy.ClassifyClose(inbound.CloseStatus, inbound.CloseDescription) == RelaySessionSignal.SessionReplaced)
                            Interlocked.Increment(ref SessionReplacedSeen);
                        return;
                    }

                    if (SelfTestTap.Inspect(_tap, envelope) == TapVerdict.Consume)
                        continue;

                    switch (envelope.Type)
                    {
                        case MessageTypes.PairSync:
                            _peers = envelope.GetPayload<PairSyncPayload>()!.PeerKeys;
                            Interlocked.Increment(ref _acceptedCount);
                            _accepted.TrySetResult();
                            break;
                        case MessageTypes.Error when envelope.GetPayload<ErrorPayload>() is { } error:
                            if (RelayConnectPolicy.ClassifyError(error.Code, error.To) == RelaySessionSignal.SessionReplaced)
                                Interlocked.Increment(ref SessionReplacedSeen);
                            if (SelfTestTap.IsSelfTestId(error.RequestId))
                                Interlocked.Increment(ref UnconsumedSelfTestReplies);
                            break;
                        case MessageTypes.Ping:
                            await SendAsync(Envelope.Create(MessageTypes.Pong, new { }), CancellationToken.None);
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                // Closed under us.
            }
        }

        public Task<SessionSnapshot> SnapshotAsync(CancellationToken ct) => Task.FromResult(new SessionSnapshot
        {
            OwnKey = key,
            PairingKeyMatchesContentId = true,
            RegisteredKey = Open && Volatile.Read(ref _acceptedCount) > 0 ? key : null,
            ContentIdKey = key,
            TokenMatchesCompiled = true,
            PartnerKeys = partner is null ? [] : [partner],
            RelayPeerKeys = _peers,
            Connected = Open,
            ConnectedUrl = Open ? url : null,
            RegisterAccepted = Open && Volatile.Read(ref _acceptedCount) > 0,
            Invariants = new SessionInvariants(Volatile.Read(ref _acceptedCount), Open, false, Volatile.Read(ref SessionReplacedSeen)),
            LastSuccessfulRelayUrl = url,
            CompiledUrls = [url],
            Identity = new RelayIdentityDiagnosis("WindUpKey.relay-identity.json", true, true, RelayIdentityFileVersion.Current,
                RelayIdentityStoreStatus.Available, true, true, false, RelayIdentityKeyRecord.Matches, 0),
        });

        public Task<IdentityCopyCheck> IdentityCopyCheckAsync(CancellationToken ct) =>
            Task.FromResult(new IdentityCopyCheck(true, RelayIdentityImport.Unchanged));

        public Task<PostPatchFacts> PostPatchFactsAsync(CancellationToken ct) => Task.FromResult(new PostPatchFacts());

        public Task<ProbeSecret?> FreshRandomSecretAsync(CancellationToken ct) =>
            Task.FromResult(ProbeSecret.FreshRandom(candidate => candidate == secret));

        public void SetTap(SelfTestTap? tap) => _tap = tap;

        public async Task<bool> SendAsync(Envelope envelope, CancellationToken ct)
        {
            if (!Open || !RelayWireRules.TryEncodeForSend(envelope, out var bytes))
                return false;
            return await SendRawAsync(bytes, ct);
        }

        public async Task<bool> SendRawAsync(byte[] utf8, CancellationToken ct)
        {
            if (!Open)
                return false;
            await _send.WaitAsync(ct);
            try
            {
                await _socket!.SendAsync(utf8, WebSocketMessageType.Text, true, ct);
                return true;
            }
            finally
            {
                _send.Release();
            }
        }

        public Task<RelayLiveState> StateAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task SetOutageHoldAsync(bool hold, CancellationToken ct) => throw new NotSupportedException();

        public bool ReconnectAllowed => false;

        public Task ReconnectAsync(RelayLiveState before, bool liftOutageHold, CancellationToken ct) => throw new NotSupportedException();

        public async ValueTask DisposeAsync()
        {
            if (_socket is { State: WebSocketState.Open } socket)
            {
                using var quick = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", quick.Token);
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
                {
                    // Gone already.
                }
            }

            if (_receive is not null)
                await _receive.WaitAsync(TimeSpan.FromSeconds(5));
            _socket?.Dispose();
        }
    }

    [LiveRelayFact]
    public async Task TheSafeTier_AgainstALiveRelay()
    {
        var url = Env("RELAY_URL");
        var token = Env("RELAY_TOKEN", "test");
        var mode = Env("MODE", "Bind");
        var key = Env("KEY", ProbePlan.RandomKey());
        var secret = Env("SECRET", RelaySecret.Generate());
        var partner = Env("PARTNER") is { Length: > 0 } p ? p : null;
        var live = Env("LIVE", "1") == "1";

        await using var host = new LiveHost(url, token, key, secret, partner);
        if (live)
            await host.StartAsync();

        using var stats = new HttpStatsClient(token);
        var suite = new SelfTestSuite(host, new ClientWebSocketProbeConnector(token), stats);
        await suite.RunSafeAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(120));
        var results = suite.Runner.Results;
        foreach (var r in results)
            Console.WriteLine(SelfTestReport.Line(r));
        var byId = results.Where(r => r.Status != CheckStatus.Info).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.Last());
        string Status(string id) => $"{id}: {byId[id].Status} {byId[id].Reason}";

        // Mode-independent: P0 through the relay, P1's limits, and nothing loud.
        Assert.True(byId["01-12"].Status == CheckStatus.Pass, Status("01-12"));
        Assert.True(byId["X-limit-binary"].Status == CheckStatus.Pass, Status("X-limit-binary"));
        Assert.True(byId["X-clock"].Status == CheckStatus.Pass, Status("X-clock"));
        Assert.DoesNotContain(results, r => r.Loud);

        if (mode == "Off")
        {
            Assert.True(byId["X-auth-canary"].Status == CheckStatus.Warn, Status("X-auth-canary"));
            Assert.Equal(CheckStatus.Skip, byId["X-auth-own-malformed"].Status);
            Assert.Equal(CheckStatus.Skip, byId["X-auth-own-random"].Status);
        }
        else
        {
            Assert.True(byId["X-auth-canary"].Status == CheckStatus.Pass, Status("X-auth-canary"));
            var expected = live ? CheckStatus.Pass : CheckStatus.Skip;
            Assert.True(byId["X-auth-own-malformed"].Status == expected, Status("X-auth-own-malformed"));
            Assert.True(byId["X-auth-own-random"].Status == expected, Status("X-auth-own-random"));
        }

        if (live)
        {
            foreach (var id in new[] { "02-5", "02-2", "03-2", "X-session" })
                Assert.True(byId[id].Status == CheckStatus.Pass, Status(id));

            // The live session is untouched: still open, never replaced, and no self-test reply reached normal handling.
            Assert.True(host.Open, "the live session is still open");
            Assert.Equal(0, host.SessionReplacedSeen);
            Assert.Equal(0, host.UnconsumedSelfTestReplies);
        }
        else
        {
            Assert.Equal(CheckStatus.Skip, byId["02-5"].Status);
            Assert.Equal(CheckStatus.Skip, byId["X-auth-own-random"].Status);
        }
    }
}
