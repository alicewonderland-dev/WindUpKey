using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using WindUpKey.Protocol;
using WindUpKey.SelfTest;
using WindUpKey.Services;

namespace WindUpKey.Tests;

/// <summary>
/// The self-test suite end to end over fakes: a scripted relay behind the probe connector (Off or Bind, 01-D4), a fake
/// host with a scripted live session, and fast timings for the opt-in verbs (04-D5 to 04-D7).
/// </summary>
public sealed class SelfTestSuiteTests
{
    private const string OwnKey = "OWNKEY01";
    private const string Url = "ws://relay.test/ws";
    private static readonly string RealSecret = RelaySecret.Generate();
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private static readonly SelfTestTimings Fast = new()
    {
        Reply = TimeSpan.FromMilliseconds(500),
        PresenceReply = TimeSpan.FromMilliseconds(700),
        Step = TimeSpan.FromSeconds(10),
        Restore = TimeSpan.FromSeconds(2),
        OutageGrace = TimeSpan.FromMilliseconds(300),
        OutageSlack = TimeSpan.FromMilliseconds(500),
        Reconnect = TimeSpan.FromMilliseconds(600),
        Poll = TimeSpan.FromMilliseconds(20),
    };

    /// <summary>A relay behind the probe connector, as 01/03 behave per AuthMode. Records every register.</summary>
    private sealed class FakeRelay(string mode, bool pre03 = false) : IProbeConnector, IStatsClient
    {
        public readonly List<(string Key, string? Secret)> Registers = [];

        public Task<ProbeConnection> ConnectAsync(Uri uri, bool withToken, TimeSpan timeout, CancellationToken ct)
        {
            if (!withToken)
                return Task.FromResult(new ProbeConnection(null, 401, "refused"));
            var (server, client) = InMemorySockets.CreatePair();
            _ = Task.Run(() => ServeAsync(server));
            return Task.FromResult(new ProbeConnection(client, 101, null));
        }

        public Task<StatsOutcome> GetAsync(Uri uri, bool withToken, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(withToken ? new StatsOutcome(200, DateTimeOffset.UtcNow, 7, null) : new StatsOutcome(401, DateTimeOffset.UtcNow, null, null));

        private async Task ServeAsync(WebSocket server)
        {
            using var _ = server;
            var reader = new BoundedMessageReader();
            try
            {
                var first = await reader.ReadAsync(server, 1 << 20, CancellationToken.None);
                if (first.Kind == WireMessageKind.Close)
                    return;
                if (first.Kind == WireMessageKind.Binary && !pre03)
                {
                    await server.CloseOutputAsync(WebSocketCloseStatus.InvalidMessageType, WireCloseReasons.BinaryNotSupported, CancellationToken.None);
                }
                else if (first.Text is { Length: > WireLimits.PreRegisterMessageBytes })
                {
                    if (pre03)
                        await Send(server, Envelope.Create(MessageTypes.Pong, new { }));
                    else
                        await server.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, WireCloseReasons.MessageTooBig, CancellationToken.None);
                }
                else if (first.Text is { } text && Envelope.TryParse(text) is { Type: MessageTypes.Register } envelope)
                {
                    var register = envelope.GetPayload<RegisterPayload>()!;
                    lock (Registers)
                        Registers.Add((register.PairingKey, register.Secret));
                    await RegisterAsync(server, register);
                }

                while ((await reader.ReadAsync(server, 1 << 20, CancellationToken.None)).Kind != WireMessageKind.Close)
                {
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
            {
                // The probe aborted.
            }
        }

        private async Task RegisterAsync(WebSocket server, RegisterPayload register)
        {
            var accepted = Envelope.Create(MessageTypes.PairSync, new PairSyncPayload());
            if (register.PairingKey == OwnKey && register.Secret == RealSecret)
            {
                await Send(server, accepted); // a takeover: never allowed to happen
                return;
            }

            switch (mode)
            {
                case "Off":
                    await Send(server, accepted);
                    return;
                case "Collide" when register.PairingKey == OwnKey:
                    // A relay that treats this key's register as legacy (a collision): never a pass.
                    await Send(server, Envelope.Create(MessageTypes.Error, new ErrorPayload { Code = ErrorCodes.PairKeyCollision }));
                    return;
                default:
                    if (!RelaySecret.IsValid(register.Secret) || register.PairingKey == OwnKey)
                    {
                        await Send(server, Envelope.Create(MessageTypes.Error, new ErrorPayload { Code = ErrorCodes.AuthFailed }));
                        await server.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, ErrorCodes.AuthFailed, CancellationToken.None);
                        return;
                    }

                    await Send(server, accepted);
                    return;
            }
        }
    }

    private static Task Send(WebSocket socket, Envelope envelope) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(envelope.Serialize()), WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>A host with a scripted live session (the relay's 02/03 replies) and scripted connection state.</summary>
    private sealed class FakeHost : ISelfTestHost, ISelfTestLink
    {
        private SelfTestTap? _tap;
        private readonly object _gate = new();
        private Stopwatch? _heldFor;

        public readonly List<Envelope> Sent = [];
        public readonly List<int> RawSizes = [];

        /// <summary>The hold and Reconnect calls in order: "hold", "lift", "start", or "lift+start" for one tick (04-D12).</summary>
        public readonly List<string> HoldEvents = [];
        public bool Connected = true;
        public bool Accepted = true;
        public int AcceptedConnections = 1;
        public int SnapshotCalls;
        public int BumpAcceptedOnSnapshot = -1;
        public bool Hold;
        public bool ReconnectWorks = true;
        public bool PartnerOnline = true;
        public int CloseCount;
        public WebSocketCloseStatus? LastCloseStatus;
        public string? LastCloseDescription;
        public string? NotAllowedTo = OwnKey;
        public string WindErrorCode = ErrorCodes.NotAllowed;
        public bool RandomKeyOnline;
        public bool PartnerSilent;
        public TimeSpan PartnerDelay = TimeSpan.Zero;
        public TimeSpan SuspendAfter = Fast.OutageGrace;
        public int IdentityCopyCheckCalls;
        public bool AllowReconnect = true;
        public int ReconnectCalls;
        public int SessionReplacedSeen;
        public bool SessionReplacedOnReconnect;
        public bool AuthBlocked;
        public string ConfigKey = OwnKey;
        public string? RegisteredKey = OwnKey;
        public string? ContentIdKey = OwnKey;

        /// <summary>04-D11: what the 64 KiB + 1 close is instead of a 1009: another PC took the key, or an auth block.</summary>
        public bool SessionReplacedOnOversize;
        public bool AuthBlockOnOversize;
        public bool SessionReplacedDuringHold;

        public ISelfTestLink Link => this;

        public Task<SessionSnapshot> SnapshotAsync(CancellationToken ct)
        {
            lock (_gate)
            {
                if (SnapshotCalls++ == BumpAcceptedOnSnapshot)
                    AcceptedConnections++;
                return Task.FromResult(new SessionSnapshot
                {
                    OwnKey = ConfigKey,
                    PairingKeyMatchesContentId = ConfigKey == ContentIdKey,
                    RegisteredKey = Connected && Accepted ? RegisteredKey : null,
                    ContentIdKey = ContentIdKey,
                    TokenMatchesCompiled = true,
                    PartnerKeys = ["PARTNER1"],
                    RelayPeerKeys = ["PARTNER1"],
                    Connected = Connected,
                    ConnectedUrl = Connected ? Url : null,
                    RegisterAccepted = Connected && Accepted,
                    Invariants = new SessionInvariants(AcceptedConnections, Connected, AuthBlocked, SessionReplacedSeen),
                    LastSuccessfulRelayUrl = Url,
                    CompiledUrls = [Url],
                    Identity = new RelayIdentityDiagnosis("WindUpKey.relay-identity.json", true, true, RelayIdentityFileVersion.Current,
                        RelayIdentityStoreStatus.Available, true, true, false, RelayIdentityKeyRecord.Matches, 0),
                    // 01-4's copy and import parts come only from IdentityCopyCheckAsync, once per run (04-D9).
                });
            }
        }

        public Task<IdentityCopyCheck> IdentityCopyCheckAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref IdentityCopyCheckCalls);
            return Task.FromResult(new IdentityCopyCheck(true, RelayIdentityImport.Unchanged));
        }

        public bool ReconnectAllowed => Volatile.Read(ref AllowReconnect);

        public Task<PostPatchFacts> PostPatchFactsAsync(CancellationToken ct) => Task.FromResult(new PostPatchFacts());

        public Task<ProbeSecret?> FreshRandomSecretAsync(CancellationToken ct) =>
            Task.FromResult(ProbeSecret.FreshRandom(candidate => candidate == RealSecret));

        public void SetTap(SelfTestTap? tap) => _tap = tap;

        public Task<bool> SendAsync(Envelope envelope, CancellationToken ct)
        {
            if (!Connected || !RelayWireRules.TryEncodeForSend(envelope, out _))
                return Task.FromResult(false);
            lock (_gate)
                Sent.Add(envelope);
            var reply = envelope.Type switch
            {
                MessageTypes.Wind => Envelope.Create(MessageTypes.Error, new ErrorPayload
                {
                    RequestId = envelope.GetPayload<WindPayload>()!.RequestId, To = NotAllowedTo, Code = WindErrorCode,
                }),
                MessageTypes.Unwind => Envelope.Create(MessageTypes.Error, new ErrorPayload
                {
                    RequestId = envelope.GetPayload<UnwindPayload>()!.RequestId, To = NotAllowedTo, Code = ErrorCodes.NotAllowed,
                }),
                MessageTypes.Ping => Envelope.Create(MessageTypes.Pong, new { }),
                MessageTypes.PresenceQuery => Envelope.Create(MessageTypes.PresenceResult, new PresenceResultPayload
                {
                    RequestId = envelope.GetPayload<PresenceQueryPayload>()!.RequestId,
                    From = envelope.GetPayload<PresenceQueryPayload>()!.To,
                    To = OwnKey,
                    Online = envelope.GetPayload<PresenceQueryPayload>()!.To == "PARTNER1" ? PartnerOnline : RandomKeyOnline,
                }),
                _ => null,
            };
            if (PartnerSilent && envelope.Type == MessageTypes.PresenceQuery && envelope.GetPayload<PresenceQueryPayload>()!.To == "PARTNER1")
                reply = null; // the partner's client never answers in time
            var delay = envelope.Type == MessageTypes.PresenceQuery && envelope.GetPayload<PresenceQueryPayload>()!.To == "PARTNER1" ? PartnerDelay : TimeSpan.Zero;
            if (reply is not null && _tap is { } tap)
                _ = Task.Run(async () =>
                {
                    await Task.Delay(delay);
                    tap.Inspect(reply);
                });
            return Task.FromResult(true);
        }

        public Task<bool> SendRawAsync(byte[] utf8, CancellationToken ct)
        {
            lock (_gate)
            {
                RawSizes.Add(utf8.Length);
                if (utf8.Length > WireLimits.RelayMessageBytes && (SessionReplacedOnOversize || AuthBlockOnOversize))
                {
                    // The live session ends blocked: the client waits for the user, and never reconnects by itself.
                    CloseCount++;
                    LastCloseStatus = WebSocketCloseStatus.PolicyViolation;
                    LastCloseDescription = SessionReplacedOnOversize ? ErrorCodes.SessionReplaced : ErrorCodes.AuthFailed;
                    if (SessionReplacedOnOversize)
                        SessionReplacedSeen++;
                    AuthBlocked |= AuthBlockOnOversize;
                    Connected = false;
                }
                else if (utf8.Length > WireLimits.RelayMessageBytes)
                {
                    // The relay closes 1009; the client reconnects by itself and is accepted.
                    CloseCount++;
                    LastCloseStatus = WebSocketCloseStatus.MessageTooBig;
                    LastCloseDescription = WireCloseReasons.MessageTooBig;
                    if (ReconnectWorks)
                        AcceptedConnections++;
                    else
                        Connected = false;
                }
            }

            return Task.FromResult(true);
        }

        public Task<RelayLiveState> StateAsync(CancellationToken ct)
        {
            lock (_gate)
            {
                var suspended = _heldFor is { } held && held.Elapsed >= SuspendAfter;
                return Task.FromResult(new RelayLiveState(Connected, Connected && Accepted, AcceptedConnections, suspended, AuthBlocked,
                    SessionReplacedSeen, CloseCount, LastCloseStatus, LastCloseDescription));
            }
        }

        public Task SetOutageHoldAsync(bool hold, CancellationToken ct)
        {
            lock (_gate)
            {
                HoldEvents.Add(hold ? "hold" : "lift");
                Hold = hold;
                if (hold)
                {
                    Connected = false;
                    _heldFor = Stopwatch.StartNew();
                    if (SessionReplacedDuringHold)
                        SessionReplacedSeen++;
                }
            }

            return Task.CompletedTask;
        }

        public Task ReconnectAsync(RelayLiveState before, bool liftOutageHold, CancellationToken ct)
        {
            lock (_gate)
            {
                HoldEvents.Add(liftOutageHold ? "lift+start" : "start");
                if (liftOutageHold)
                    Hold = false;
                ReconnectCalls++;
                if (SessionReplacedOnReconnect)
                    SessionReplacedSeen++;
                if (ReconnectWorks && !Hold)
                {
                    Connected = true;
                    Accepted = true;
                    AcceptedConnections++;
                    _heldFor = null;
                }
            }

            return Task.CompletedTask;
        }
    }

    private static Dictionary<string, CheckResult> ById(SelfTestSuite suite) =>
        suite.Runner.Results.Where(r => r.Status != CheckStatus.Info).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.Last());

    [Fact]
    public async Task InBind_TheSafeTierPasses_AndOwnKeyProbesCarryOnlyWrongSecrets()
    {
        var relay = new FakeRelay("Bind");
        var host = new FakeHost();
        var suite = new SelfTestSuite(host, relay, relay, Fast);
        await suite.RunSafeAsync(CancellationToken.None).WaitAsync(Wait);
        var byId = ById(suite);

        foreach (var id in new[] { "01-12", "03-2", "X-limit-binary", "X-auth-canary", "X-auth-own-malformed", "X-auth-own-random", "02-5", "02-2", "X-session" })
            Assert.True(byId[id].Status == CheckStatus.Pass, $"{id}: {byId[id].Status} {byId[id].Reason}");

        // The canary is a random key; the own-key probes are P3′ (malformed) then P3 (fresh random), never the real secret.
        Assert.Equal(3, relay.Registers.Count);
        Assert.NotEqual(OwnKey, relay.Registers[0].Key);
        Assert.Equal(ProbeSecret.MalformedValue, relay.Registers[0].Secret);
        Assert.Equal((OwnKey, ProbeSecret.MalformedValue), relay.Registers[1]);
        Assert.Equal(OwnKey, relay.Registers[2].Key);
        Assert.True(RelaySecret.IsValid(relay.Registers[2].Secret));
        Assert.DoesNotContain(relay.Registers, r => r.Secret == RealSecret);

        // Every reported id is in the catalog, and the live session got at most the budget of rate-counted messages.
        Assert.All(suite.Runner.Results, r => Assert.NotNull(SelfTestCatalog.Find(r.Id)));
        Assert.True(host.Sent.Count(e => e.Type != MessageTypes.Ping) <= ProbePlan.RateBudget);
        var wind = host.Sent.Single(e => e.Type == MessageTypes.Wind).GetPayload<WindPayload>()!;
        Assert.StartsWith(SelfTestTap.RequestIdPrefix, wind.RequestId);
        Assert.NotEqual(OwnKey, wind.To);
        Assert.NotEqual("PARTNER1", wind.To);
        Assert.Equal(1.0 / 3600, wind.Hours); // one second: harmless even if a relay forwarded it
    }

    [Fact]
    public async Task InOff_NoOwnKeyRegisterIsEverSent()
    {
        var relay = new FakeRelay("Off");
        var suite = new SelfTestSuite(new FakeHost(), relay, relay, Fast);
        await suite.RunSafeAsync(CancellationToken.None).WaitAsync(Wait);
        var byId = ById(suite);

        Assert.Equal(CheckStatus.Warn, byId["X-auth-canary"].Status);
        Assert.Equal(CheckStatus.Skip, byId["X-auth-own-malformed"].Status);
        Assert.Equal(CheckStatus.Skip, byId["X-auth-own-random"].Status);
        var only = Assert.Single(relay.Registers);
        Assert.NotEqual(OwnKey, only.Key);
    }

    [Fact]
    public async Task Disconnected_NoOwnKeyProbe_NoLiveMessage()
    {
        var relay = new FakeRelay("Bind");
        var host = new FakeHost { Connected = false };
        var suite = new SelfTestSuite(host, relay, relay, Fast);
        await suite.RunSafeAsync(CancellationToken.None).WaitAsync(Wait);
        var byId = ById(suite);

        Assert.Equal(CheckStatus.Pass, byId["X-auth-canary"].Status);
        Assert.Equal(CheckStatus.Skip, byId["X-auth-own-random"].Status);
        Assert.Equal(CheckStatus.Skip, byId["02-5"].Status);
        Assert.DoesNotContain(relay.Registers, r => r.Key == OwnKey);
        Assert.Empty(host.Sent);
    }

    [Fact]
    public async Task AFailedMalformedProbe_StopsBeforeTheRandomSecretOne()
    {
        var relay = new FakeRelay("Collide");
        var suite = new SelfTestSuite(new FakeHost(), relay, relay, Fast);
        await suite.RunSafeAsync(CancellationToken.None).WaitAsync(Wait);
        var byId = ById(suite);
        Assert.Equal(CheckStatus.Fail, byId["X-auth-own-malformed"].Status);
        Assert.Equal(CheckStatus.Skip, byId["X-auth-own-random"].Status);
        Assert.Single(relay.Registers, r => r.Key == OwnKey);
        Assert.Equal(ProbeSecret.MalformedValue, relay.Registers.Single(r => r.Key == OwnKey).Secret);
    }

    [Fact]
    public async Task AnOlderRelay_GetsNoWind()
    {
        var relay = new FakeRelay("Bind", pre03: true);
        var host = new FakeHost();
        var suite = new SelfTestSuite(host, relay, relay, Fast);
        await suite.RunSafeAsync(CancellationToken.None).WaitAsync(Wait);
        var byId = ById(suite);

        Assert.Equal(CheckStatus.Skip, byId["02-5"].Status);
        Assert.DoesNotContain(host.Sent, e => e.Type is MessageTypes.Wind or MessageTypes.Unwind);
        Assert.Equal(CheckStatus.Warn, byId["03-2"].Status);
    }

    [Fact]
    public async Task ANotAllowedForSomeoneElse_IsNotAPass()
    {
        var relay = new FakeRelay("Bind");
        var suite = new SelfTestSuite(new FakeHost { NotAllowedTo = "OTHERKEY" }, relay, relay, Fast);
        await suite.RunSafeAsync(CancellationToken.None).WaitAsync(Wait);
        Assert.Equal(CheckStatus.Fail, ById(suite)["02-5"].Status);
    }

    [Fact]
    public async Task AQueuedWind_IsNotARefusal()
    {
        var relay = new FakeRelay("Bind");
        var suite = new SelfTestSuite(new FakeHost { WindErrorCode = ErrorCodes.QueueFull }, relay, relay, Fast);
        await suite.RunSafeAsync(CancellationToken.None).WaitAsync(Wait);
        Assert.Equal(CheckStatus.Fail, ById(suite)["02-5"].Status);
    }

    [Fact]
    public async Task ARandomKeyThatLooksOnline_FailsPresence()
    {
        var relay = new FakeRelay("Bind");
        var suite = new SelfTestSuite(new FakeHost { RandomKeyOnline = true }, relay, relay, Fast);
        await suite.RunSafeAsync(CancellationToken.None).WaitAsync(Wait);
        Assert.Equal(CheckStatus.Fail, ById(suite)["02-2"].Status);
    }

    [Fact]
    public async Task AReconnectBeforeTheOwnKeyProbes_SkipsThem()
    {
        var relay = new FakeRelay("Bind");
        var host = new FakeHost { BumpAcceptedOnSnapshot = 1 }; // the re-check right before P3′ sees a new session
        var suite = new SelfTestSuite(host, relay, relay, Fast);
        await suite.RunSafeAsync(CancellationToken.None).WaitAsync(Wait);
        Assert.Equal(CheckStatus.Skip, ById(suite)["X-auth-own-malformed"].Status);
        Assert.DoesNotContain(relay.Registers, r => r.Key == OwnKey);
    }

    [Fact]
    public async Task ASessionChangeDuringTheRun_SkipsWhatDependsOnIt()
    {
        var relay = new FakeRelay("Bind");
        var host = new FakeHost { BumpAcceptedOnSnapshot = 3 }; // before, own-key re-check, live re-check, then after
        var suite = new SelfTestSuite(host, relay, relay, Fast);
        await suite.RunSafeAsync(CancellationToken.None).WaitAsync(Wait);
        var byId = ById(suite);

        Assert.Equal(CheckStatus.Skip, byId["02-5"].Status);
        Assert.Equal(CheckStatus.Skip, byId["02-2"].Status);
        Assert.Equal(CheckStatus.Skip, byId["03-2"].Status);
        Assert.Equal(CheckStatus.Pass, byId["01-12"].Status);
        Assert.Equal(CheckStatus.Warn, byId["X-session"].Status);
    }

    [Fact]
    public async Task AReconnectBeforeTheLiveChecks_SendsNothingOnTheLiveSession()
    {
        var relay = new FakeRelay("Bind");
        var host = new FakeHost { BumpAcceptedOnSnapshot = 2 }; // the re-check right before the live checks sees a new session
        var suite = new SelfTestSuite(host, relay, relay, Fast);
        await suite.RunSafeAsync(CancellationToken.None).WaitAsync(Wait);
        var byId = ById(suite);
        Assert.Equal(CheckStatus.Skip, byId["02-5"].Status);
        Assert.Equal(CheckStatus.Skip, byId["02-2"].Status);
        Assert.Empty(host.Sent);
    }

    [Fact]
    public void TheLiveReCheck_WantsTheSameAcceptedSessionOnTheProbedHost()
    {
        var before = new SessionSnapshot
        {
            Connected = true,
            RegisterAccepted = true,
            ConnectedUrl = Url,
            Invariants = new SessionInvariants(2, true, false, 0),
        };
        Assert.Null(SelfTestSuite.LiveSessionChanged(before, before, Url));
        Assert.Null(SelfTestSuite.LiveSessionChanged(before, before with { ConnectedUrl = Url.ToUpperInvariant() }, Url));
        Assert.NotNull(SelfTestSuite.LiveSessionChanged(before, before with { Invariants = before.Invariants with { AcceptedConnections = 3 } }, Url));
        Assert.NotNull(SelfTestSuite.LiveSessionChanged(before, before with { Invariants = before.Invariants with { Open = false } }, Url));
        Assert.NotNull(SelfTestSuite.LiveSessionChanged(before, before with { RegisterAccepted = false }, Url));
        Assert.NotNull(SelfTestSuite.LiveSessionChanged(before, before with { ConnectedUrl = "ws://other.test/ws" }, Url));
    }

    /// <summary>A partner's presence wait longer than the other replies, and longer than two ordinary steps (04-D10).</summary>
    private static readonly SelfTestTimings SlowPresence = Fast with { Step = TimeSpan.FromSeconds(1), PresenceReply = TimeSpan.FromSeconds(2.5) };

    [Fact]
    public async Task AnUnansweredPartner_IsAWarn_NotAFail_AndTheLiveStepWaitsForIt()
    {
        var relay = new FakeRelay("Bind");
        var suite = new SelfTestSuite(new FakeHost { PartnerSilent = true }, relay, relay, SlowPresence);
        await suite.RunSafeAsync(CancellationToken.None).WaitAsync(Wait);
        var presence = ById(suite)["02-2"];
        Assert.True(presence.Status == CheckStatus.Warn, $"{presence.Status}: {presence.Reason}");
        Assert.Contains("unanswered", presence.Reason);
    }

    [Fact]
    public async Task APartnerSlowerThanAReply_ButWithinThePresenceTimeout_Passes()
    {
        var relay = new FakeRelay("Bind");
        var suite = new SelfTestSuite(new FakeHost { PartnerDelay = TimeSpan.FromSeconds(1) }, relay, relay, SlowPresence);
        await suite.RunSafeAsync(CancellationToken.None).WaitAsync(Wait);
        var presence = ById(suite)["02-2"];
        Assert.True(presence.Status == CheckStatus.Pass, $"{presence.Status}: {presence.Reason}");
    }

    [Fact]
    public async Task TheImportCheck_RunsOncePerRun_NotPerSnapshot()
    {
        var relay = new FakeRelay("Bind");
        var host = new FakeHost();
        var suite = new SelfTestSuite(host, relay, relay, Fast);
        await suite.RunSafeAsync(CancellationToken.None).WaitAsync(Wait);
        Assert.Equal(1, host.IdentityCopyCheckCalls);
        Assert.True(host.SnapshotCalls >= 3);
        Assert.Equal(CheckStatus.Pass, ById(suite)["01-4"].Status);
    }

    // ---------- 04-D7 verbs ----------

    [Fact]
    public async Task Outage_SuspendsAfterTheGrace_ThenReconnects_AndLiftsTheHold()
    {
        var host = new FakeHost();
        var suite = new SelfTestSuite(host, new FakeRelay("Bind"), new FakeRelay("Bind"), Fast);
        await suite.RunVerbAsync(SelfTestTier.Outage, CancellationToken.None).WaitAsync(Wait);
        var result = ById(suite)["01-9"];
        Assert.True(result.Status == CheckStatus.Pass, result.Reason);
        Assert.False(host.Hold);
        Assert.True(host.Connected);
    }

    [Fact]
    public async Task Outage_LiftsTheHoldInTheSameTickAsStart()
    {
        // 04-D12: no separate lift before Start(), which would let the connect loop reconnect in between.
        var host = new FakeHost();
        var suite = new SelfTestSuite(host, new FakeRelay("Bind"), new FakeRelay("Bind"), Fast);
        await suite.RunVerbAsync(SelfTestTier.Outage, CancellationToken.None).WaitAsync(Wait);
        Assert.True(ById(suite)["01-9"].Status == CheckStatus.Pass, ById(suite)["01-9"].Reason);
        Assert.Equal(["hold", "lift+start"], host.HoldEvents.Take(2));
        Assert.Equal(1, host.ReconnectCalls);
        Assert.False(host.Hold);
    }

    [Fact]
    public async Task Outage_SuspendedBeforeTheGrace_Fails()
    {
        var host = new FakeHost { SuspendAfter = TimeSpan.Zero };
        var suite = new SelfTestSuite(host, new FakeRelay("Bind"), new FakeRelay("Bind"), Fast);
        await suite.RunVerbAsync(SelfTestTier.Outage, CancellationToken.None).WaitAsync(Wait);
        Assert.Equal(CheckStatus.Fail, ById(suite)["01-9"].Status);
        Assert.False(host.Hold);
    }

    [Fact]
    public async Task Outage_ThatCantReconnect_IsALoudFail_AndTheHoldIsStillLifted()
    {
        var host = new FakeHost { ReconnectWorks = false };
        var suite = new SelfTestSuite(host, new FakeRelay("Bind"), new FakeRelay("Bind"), Fast);
        await suite.RunVerbAsync(SelfTestTier.Outage, CancellationToken.None).WaitAsync(Wait);
        var fails = suite.Runner.Results.Where(r => r.Id == "01-9").ToList();
        Assert.All(fails, r => Assert.True(r is { Status: CheckStatus.Fail, Loud: true }));
        Assert.Contains(fails, r => r.Reason.Contains("RESTORE FAILED"));
        Assert.False(host.Hold);
    }

    [Fact]
    public async Task Outage_Stopped_StillRestores()
    {
        var host = new FakeHost();
        var suite = new SelfTestSuite(host, new FakeRelay("Bind"), new FakeRelay("Bind"), Fast with { OutageGrace = TimeSpan.FromSeconds(20) });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => suite.RunVerbAsync(SelfTestTier.Outage, cts.Token).WaitAsync(Wait));
        Assert.False(host.Hold);
        Assert.True(host.Connected);
    }

    [Fact]
    public async Task Oversize_Gets1009_AndAnAcceptedReconnect()
    {
        var host = new FakeHost();
        var suite = new SelfTestSuite(host, new FakeRelay("Bind"), new FakeRelay("Bind"), Fast);
        await suite.RunVerbAsync(SelfTestTier.Oversize, CancellationToken.None).WaitAsync(Wait);
        var result = ById(suite)["03-2"];
        Assert.True(result.Status == CheckStatus.Pass, result.Reason);
        Assert.Equal([WireLimits.RelayMessageBytes + 1], host.RawSizes);
    }

    [Fact]
    public async Task Verbs_DoNothing_WithoutAnAcceptedSession()
    {
        var host = new FakeHost { Connected = false };
        var suite = new SelfTestSuite(host, new FakeRelay("Bind"), new FakeRelay("Bind"), Fast);
        await suite.RunVerbAsync(SelfTestTier.Oversize, CancellationToken.None).WaitAsync(Wait);
        Assert.Equal(CheckStatus.Skip, ById(suite)["03-2"].Status);
        Assert.Empty(host.RawSizes);
    }

    [Fact]
    public async Task Reconnect_IsAcceptedOrFails()
    {
        var host = new FakeHost();
        var suite = new SelfTestSuite(host, new FakeRelay("Bind"), new FakeRelay("Bind"), Fast);
        await suite.RunVerbAsync(SelfTestTier.Reconnect, CancellationToken.None).WaitAsync(Wait);
        Assert.Equal(CheckStatus.Pass, ById(suite)["V-reconnect"].Status);

        var stuck = new FakeHost { ReconnectWorks = false };
        var failing = new SelfTestSuite(stuck, new FakeRelay("Bind"), new FakeRelay("Bind"), Fast);
        await failing.RunVerbAsync(SelfTestTier.Reconnect, CancellationToken.None).WaitAsync(Wait);
        Assert.Equal(CheckStatus.Fail, failing.Runner.Results.First(r => r.Id == "V-reconnect").Status);
    }

    [Fact]
    public async Task ARestoreAfterUnloadOrLogout_LiftsTheHold_ButNeverStartsTheRelay()
    {
        // Dispose or logout: the controller sets "no reconnect", then cancels the run (04-D10).
        var host = new FakeHost();
        var suite = new SelfTestSuite(host, new FakeRelay("Bind"), new FakeRelay("Bind"), Fast with { OutageGrace = TimeSpan.FromSeconds(20) });
        using var cts = new CancellationTokenSource();
        var run = suite.RunVerbAsync(SelfTestTier.Outage, cts.Token);
        await Task.Delay(200);
        Volatile.Write(ref host.AllowReconnect, false);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Wait));
        Assert.False(host.Hold);
        Assert.Equal(0, host.ReconnectCalls);
        Assert.DoesNotContain(suite.Runner.Results, r => r.Loud);
        Assert.Contains(suite.Runner.Results, r => r is { Id: "X-restore", Status: CheckStatus.Info });

        // Signalled before a verb that would reconnect: still no Start().
        foreach (var verb in new[] { SelfTestTier.Reconnect, SelfTestTier.Oversize })
        {
            var stopped = new FakeHost { AllowReconnect = false, ReconnectWorks = false };
            var verbSuite = new SelfTestSuite(stopped, new FakeRelay("Bind"), new FakeRelay("Bind"), Fast);
            await verbSuite.RunVerbAsync(verb, CancellationToken.None).WaitAsync(Wait);
            Assert.Equal(0, stopped.ReconnectCalls);
        }
    }

    [Fact]
    public async Task AVerb_WarnsOnASessionReplacedItDidntCause()
    {
        var host = new FakeHost { SessionReplacedOnReconnect = true };
        var suite = new SelfTestSuite(host, new FakeRelay("Bind"), new FakeRelay("Bind"), Fast);
        await suite.RunVerbAsync(SelfTestTier.Reconnect, CancellationToken.None).WaitAsync(Wait);
        var session = suite.Runner.Results.Last(r => r.Id == "X-session");
        Assert.True(session.Status == CheckStatus.Warn, $"{session.Status}: {session.Reason}");
        Assert.Contains("session_replaced", session.Reason);

        var clean = new SelfTestSuite(new FakeHost(), new FakeRelay("Bind"), new FakeRelay("Bind"), Fast);
        await clean.RunVerbAsync(SelfTestTier.Reconnect, CancellationToken.None).WaitAsync(Wait);
        Assert.Equal(CheckStatus.Info, clean.Runner.Results.Last(r => r.Id == "X-session").Status); // the reconnect it intends
    }

    // ---------- 04-D11 ----------

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ASessionReplacedOrAuthBlockDuringAVerb_IsNeverReconnectedOver(bool sessionReplaced, bool authBlock)
    {
        var host = new FakeHost { SessionReplacedOnOversize = sessionReplaced, AuthBlockOnOversize = authBlock };
        var suite = new SelfTestSuite(host, new FakeRelay("Bind"), new FakeRelay("Bind"), Fast);
        await suite.RunVerbAsync(SelfTestTier.Oversize, CancellationToken.None).WaitAsync(Wait);

        Assert.Equal(0, host.ReconnectCalls);
        Assert.False(host.Hold);
        var takeover = Assert.Single(suite.Runner.Results, r => r.Reason.Contains("press Reconnect in the Pairing tab yourself"));
        Assert.True(takeover is { Id: "03-2", Status: CheckStatus.Fail, Loud: true }, takeover.Reason);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ATakeoverOnTheOversizeClose_IsOneFail_WithoutWaitingForAReconnect(bool sessionReplaced, bool authBlock)
    {
        // 04-D12: the client won't reconnect by itself after a takeover, so the verb doesn't wait 20 s for it, and
        // doesn't add a "didn't reconnect after the 1009 close" FAIL.
        var host = new FakeHost { SessionReplacedOnOversize = sessionReplaced, AuthBlockOnOversize = authBlock };
        var suite = new SelfTestSuite(host, new FakeRelay("Bind"), new FakeRelay("Bind"), Fast with { Reconnect = TimeSpan.FromSeconds(20) });
        await suite.RunVerbAsync(SelfTestTier.Oversize, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        var fail = Assert.Single(suite.Runner.Results, r => r.Status == CheckStatus.Fail);
        Assert.True(fail is { Id: "03-2", Loud: true }, fail.Reason);
        Assert.Contains("press Reconnect in the Pairing tab yourself", fail.Reason);
        Assert.DoesNotContain(suite.Runner.Results, r => r.Reason.Contains("didn't reconnect") || r.Reason.Contains("1009"));
        Assert.Equal(0, host.ReconnectCalls);
        Assert.False(host.Hold);
    }

    [Fact]
    public async Task ASessionReplacedDuringTheOutage_IsNotReconnectedOver_AndReportedOnce()
    {
        var host = new FakeHost { SessionReplacedDuringHold = true };
        var suite = new SelfTestSuite(host, new FakeRelay("Bind"), new FakeRelay("Bind"), Fast);
        await suite.RunVerbAsync(SelfTestTier.Outage, CancellationToken.None).WaitAsync(Wait);

        Assert.Equal(0, host.ReconnectCalls);
        Assert.False(host.Hold);
        var takeover = Assert.Single(suite.Runner.Results, r => r.Reason.Contains("press Reconnect in the Pairing tab yourself"));
        Assert.True(takeover is { Id: "01-9", Status: CheckStatus.Fail, Loud: true }, takeover.Reason);
    }

    [Fact]
    public void TheTakeoverRisk_IsASessionReplacedOrANewAuthBlock()
    {
        var before = new RelayLiveState(true, true, 1, false, false, 0, 0, null, null);
        Assert.Null(SelfTestSuite.TakeoverRisk(before, before));
        Assert.Null(SelfTestSuite.TakeoverRisk(before, before with { Connected = false, AcceptedConnections = 2, CloseCount = 1 }));
        Assert.NotNull(SelfTestSuite.TakeoverRisk(before, before with { SessionReplacedSeen = 1 }));
        Assert.NotNull(SelfTestSuite.TakeoverRisk(before, before with { AuthBlocked = true }));
        var blocked = before with { AuthBlocked = true };
        Assert.Null(SelfTestSuite.TakeoverRisk(blocked, blocked));
    }

    [Fact]
    public async Task OwnKeyProbes_AddressTheRegisteredKey_NotTheConfigKey()
    {
        var relay = new FakeRelay("Bind");
        var host = new FakeHost { ConfigKey = "CFGKEY99" }; // the config key changed since the live session registered
        var suite = new SelfTestSuite(host, relay, relay, Fast);
        await suite.RunSafeAsync(CancellationToken.None).WaitAsync(Wait);

        Assert.DoesNotContain(relay.Registers, r => r.Key == "CFGKEY99");
        Assert.Equal(2, relay.Registers.Count(r => r.Key == OwnKey));
        Assert.Equal(CheckStatus.Pass, ById(suite)["X-auth-own-malformed"].Status);
    }

    [Theory]
    [InlineData(null, OwnKey)]
    [InlineData(OwnKey, null)]
    [InlineData(OwnKey, "DERIVED1")]
    public async Task OwnKeyProbes_SkipUnlessTheRegisteredKeyIsTheContentIdKey(string? registered, string? derived)
    {
        var relay = new FakeRelay("Bind");
        var host = new FakeHost { RegisteredKey = registered, ContentIdKey = derived };
        var suite = new SelfTestSuite(host, relay, relay, Fast);
        await suite.RunSafeAsync(CancellationToken.None).WaitAsync(Wait);
        var byId = ById(suite);

        Assert.Equal(CheckStatus.Skip, byId["X-auth-own-malformed"].Status);
        Assert.Equal(CheckStatus.Skip, byId["X-auth-own-random"].Status);
        Assert.Contains("registered with", byId["X-auth-own-malformed"].Reason);
        Assert.Single(relay.Registers); // the canary only
        Assert.DoesNotContain(relay.Registers, r => r.Key == OwnKey || r.Key == "DERIVED1");
    }
}
