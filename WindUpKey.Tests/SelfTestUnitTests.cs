using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json.Nodes;
using WindUpKey.Protocol;
using WindUpKey.SelfTest;
using WindUpKey.Services;

namespace WindUpKey.Tests;

/// <summary>
/// Issue 04's Dalamud-free self-test units: the catalog (04-D4), probe classifiers and gating (04-D5), the tap
/// (04-D6), redaction and the report (04-D3), the runner (restores, invariants), and the post-patch judge (04-D8).
/// </summary>
public sealed class SelfTestUnitTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    // ---------- catalog (04-D4) ----------

    [Fact]
    public void Catalog_HasThe19ChecklistItems_InChecklistOrder()
    {
        Assert.Equal(
            [
                "01-1", "01-2", "01-3", "01-4", "01-5", "01-6", "01-7", "01-8", "01-9", "01-10", "01-11", "01-12",
                "02-1", "02-2", "02-3", "02-4", "02-5", "03-1", "03-2",
            ],
            SelfTestCatalog.Checklist.Select(e => e.Id));
    }

    [Fact]
    public void Catalog_ClassifiesTheChecklistAsTheOwnerDecided()
    {
        string[] Ids(CheckCoverage c) => SelfTestCatalog.Checklist.Where(e => e.Coverage == c).Select(e => e.Id).ToArray();
        Assert.Equal(["01-12", "02-5"], Ids(CheckCoverage.Full));
        Assert.Equal(["01-1", "01-2", "01-4", "01-10", "01-11", "02-2", "02-3", "02-4", "03-2"], Ids(CheckCoverage.Partial));
        Assert.Equal(["01-9"], Ids(CheckCoverage.OptIn));
        Assert.Equal(["01-3", "01-5", "01-6", "01-7", "01-8", "02-1", "03-1"], Ids(CheckCoverage.Manual));
    }

    [Fact]
    public void Catalog_EveryManualPartHasSteps_AndEveryOptInHasARestore()
    {
        foreach (var entry in SelfTestCatalog.All)
        {
            if (entry.Coverage is CheckCoverage.Partial or CheckCoverage.Manual or CheckCoverage.OptIn)
                Assert.True(entry.ManualSteps.Count > 0, $"{entry.Id} has no manual steps");
            if (entry.Coverage == CheckCoverage.OptIn)
            {
                Assert.False(string.IsNullOrWhiteSpace(entry.Restore), $"{entry.Id} has no restore");
                Assert.False(string.IsNullOrWhiteSpace(entry.OptInVerb), $"{entry.Id} has no verb");
            }
        }

        Assert.Equal(SelfTestCatalog.All.Count(), SelfTestCatalog.All.Select(e => e.Id).Distinct().Count());
        Assert.NotEmpty(SelfTestCatalog.ManualText("01-3"));
        Assert.Empty(SelfTestCatalog.ManualText("nope"));
    }

    // ---------- secrets (04-D5) ----------

    [Fact]
    public void TheMalformedSecret_IsNeverValid_EvenAfterNormalising()
    {
        Assert.False(RelaySecret.IsValid(ProbeSecret.MalformedValue));
        Assert.False(RelaySecret.IsValid(RelaySecret.Normalize(ProbeSecret.MalformedValue)));
        Assert.Null(RelaySecret.HashHex(ProbeSecret.MalformedValue));
        Assert.False(string.IsNullOrWhiteSpace(ProbeSecret.Malformed.WireValue));
        Assert.Equal(ProbeSecret.MalformedValue, ProbeSecret.Malformed.WireValue);
    }

    [Fact]
    public void AProbeSecret_CanNeverBeMadeFromAString()
    {
        // The real secret can't be passed to a probe by construction: nothing callable outside the type takes a string.
        var type = typeof(ProbeSecret);
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        foreach (var ctor in type.GetConstructors(all))
            Assert.True(ctor.IsPrivate, "every constructor is private");
        foreach (var method in type.GetMethods(all).Where(m => m.DeclaringType == type && !m.IsPrivate && !m.IsSpecialName))
            Assert.DoesNotContain(method.GetParameters(), p => p.ParameterType == typeof(string));
        foreach (var property in type.GetProperties(all).Where(p => p.DeclaringType == type))
            Assert.True(property.SetMethod is null || property.SetMethod.IsPrivate, $"{property.Name} can't be set from outside");
    }

    [Fact]
    public void AFreshRandomSecret_IsValid_AndNeverTheCurrentOrPreviousOne()
    {
        var seen = new List<string>();
        var fresh = ProbeSecret.FreshRandom(candidate =>
        {
            seen.Add(candidate);
            return seen.Count < 3; // the first two candidates "are" the current and previous secret
        });
        Assert.NotNull(fresh);
        Assert.Equal(ProbeSecretKind.FreshRandom, fresh.Kind);
        Assert.Equal(seen[2], fresh.WireValue);
        Assert.True(RelaySecret.IsValid(fresh.WireValue));
        Assert.Null(ProbeSecret.FreshRandom(_ => true));
    }

    // ---------- classifiers (04-D5) ----------

    [Fact]
    public void PairSync_IsNeverAPass()
    {
        var accepted = new ProbeOutcome(ProbeOutcomeKind.Accepted);
        Assert.NotEqual(AuthActivity.Active, ProbeJudge.Canary(accepted));
        Assert.NotEqual(CheckStatus.Pass, ProbeJudge.CanaryResult(accepted).Status);
        var own = ProbeJudge.OwnKey("X-auth-own-random", accepted);
        Assert.Equal(CheckStatus.Fail, own.Status);
        Assert.True(own.Loud);
        Assert.Contains("reset-account", own.Reason);
        var replaced = ProbeJudge.OwnKey("X-auth-own-random", new ProbeOutcome(ProbeOutcomeKind.SessionReplaced));
        Assert.True(replaced is { Status: CheckStatus.Fail, Loud: true });
    }

    [Fact]
    public void ACollision_IsNeverAPass()
    {
        var collision = new ProbeOutcome(ProbeOutcomeKind.Collision);
        Assert.Equal(CheckStatus.Fail, ProbeJudge.OwnKey("X-auth-own-malformed", collision).Status);
        Assert.NotEqual(AuthActivity.Active, ProbeJudge.Canary(collision));
    }

    [Fact]
    public void OnlyAuthFailed_MakesTheCanaryActive_AndPassesAnOwnKeyProbe()
    {
        var failed = new ProbeOutcome(ProbeOutcomeKind.AuthFailed);
        Assert.Equal(AuthActivity.Active, ProbeJudge.Canary(failed));
        Assert.Equal(CheckStatus.Pass, ProbeJudge.OwnKey("X-auth-own-random", failed).Status);
        foreach (var kind in Enum.GetValues<ProbeOutcomeKind>().Where(k => k != ProbeOutcomeKind.AuthFailed))
        {
            Assert.NotEqual(AuthActivity.Active, ProbeJudge.Canary(new ProbeOutcome(kind)));
            Assert.NotEqual(CheckStatus.Pass, ProbeJudge.OwnKey("X-auth-own-random", new ProbeOutcome(kind)).Status);
        }
    }

    [Fact]
    public void ACloseWithoutItsDescription_Fails_AndIsRetriedOnce()
    {
        var bare = new ProbeOutcome(ProbeOutcomeKind.Closed, CloseStatus: WebSocketCloseStatus.MessageTooBig, CloseDescription: string.Empty);
        var first = ProbeJudge.Limit("X", bare, WebSocketCloseStatus.MessageTooBig, WireCloseReasons.MessageTooBig, retried: false);
        Assert.Equal(CheckStatus.Fail, first.Result.Status);
        Assert.True(first.Retry);
        Assert.True(first.ProvesRelay03);
        var second = ProbeJudge.Limit("X", bare, WebSocketCloseStatus.MessageTooBig, WireCloseReasons.MessageTooBig, retried: true);
        Assert.Equal(CheckStatus.Fail, second.Result.Status);
        Assert.False(second.Retry);

        var intact = bare with { CloseDescription = WireCloseReasons.MessageTooBig };
        Assert.Equal(CheckStatus.Pass, ProbeJudge.Limit("X", intact, WebSocketCloseStatus.MessageTooBig, WireCloseReasons.MessageTooBig, false).Result.Status);
        var wrongCode = intact with { CloseStatus = WebSocketCloseStatus.PolicyViolation };
        var verdict = ProbeJudge.Limit("X", wrongCode, WebSocketCloseStatus.MessageTooBig, WireCloseReasons.MessageTooBig, false);
        Assert.Equal(CheckStatus.Fail, verdict.Result.Status);
        Assert.False(verdict.ProvesRelay03);
        var pong = ProbeJudge.Limit("X", new ProbeOutcome(ProbeOutcomeKind.Pong), WebSocketCloseStatus.MessageTooBig, WireCloseReasons.MessageTooBig, false);
        Assert.Equal(CheckStatus.Warn, pong.Result.Status);
        Assert.False(pong.ProvesRelay03);
    }

    [Fact]
    public void TheFunnelCheck_NeedsAll4Answers()
    {
        var refused = new ProbeOutcome(ProbeOutcomeKind.HttpRefused, 401);
        var upgraded = new ProbeOutcome(ProbeOutcomeKind.Upgraded, 101);
        var s401 = new StatsOutcome(401, null, null, null);
        var s200 = new StatsOutcome(200, null, 3, null);
        Assert.Equal(CheckStatus.Pass, ProbeJudge.Funnel("h", refused, upgraded, s401, s200).Status);
        var noTokenAccepted = ProbeJudge.Funnel("h", upgraded, upgraded, s401, s200);
        Assert.Equal(CheckStatus.Fail, noTokenAccepted.Status);
        Assert.Contains("without the token was accepted", noTokenAccepted.Reason); // points at a relay with no token configured
        Assert.Equal(CheckStatus.Fail, ProbeJudge.Funnel("h", refused, refused, s401, s200).Status);
        Assert.Equal(CheckStatus.Fail, ProbeJudge.Funnel("h", refused, upgraded, s200, s200).Status);
        Assert.Equal(CheckStatus.Fail, ProbeJudge.Funnel("h", refused, upgraded, s401, s401).Status);
    }

    [Fact]
    public void ClockSkew_WarnsOver2Minutes()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(CheckStatus.Pass, ProbeJudge.Clock(now.AddSeconds(-110), now).Status);
        Assert.Equal(CheckStatus.Warn, ProbeJudge.Clock(now.AddSeconds(130), now).Status);
        Assert.Equal(CheckStatus.Skip, ProbeJudge.Clock(null, now).Status);
    }

    // ---------- gating (04-D5, 04-D6) ----------

    [Fact]
    public void OwnKeyProbes_RunOnlyWithAuthActive_TheRegisterAccepted_AndTheSameHost()
    {
        const string live = "wss://a.example/ws";
        Assert.True(ProbePlan.OwnKeyProbesAllowed(AuthActivity.Active, true, live, live));
        Assert.True(ProbePlan.OwnKeyProbesAllowed(AuthActivity.Active, true, live, "WSS://A.EXAMPLE/ws"));
        Assert.False(ProbePlan.OwnKeyProbesAllowed(AuthActivity.Inactive, true, live, live));
        Assert.False(ProbePlan.OwnKeyProbesAllowed(AuthActivity.Unknown, true, live, live));
        Assert.False(ProbePlan.OwnKeyProbesAllowed(AuthActivity.Active, false, live, live));
        Assert.False(ProbePlan.OwnKeyProbesAllowed(AuthActivity.Active, true, live, "wss://b.example/ws"));
        Assert.False(ProbePlan.OwnKeyProbesAllowed(AuthActivity.Active, true, live, null));
        Assert.False(ProbePlan.OwnKeyProbesAllowed(AuthActivity.Active, true, null, null));
    }

    [Fact]
    public void TheWindProbe_RunsOnlyAfterA1009()
    {
        Assert.True(ProbePlan.WindProbeAllowed(relayIs03OrNewer: true, liveRegisterAccepted: true));
        Assert.False(ProbePlan.WindProbeAllowed(relayIs03OrNewer: false, liveRegisterAccepted: true));
        Assert.False(ProbePlan.WindProbeAllowed(relayIs03OrNewer: true, liveRegisterAccepted: false));
    }

    [Fact]
    public void TheRandomUnpairedKey_IsNeverOurs_APartners_OrAPendingOne()
    {
        var queue = new Queue<string>(["OWNKEY01", "partner1", "PEND0001", "REMOVE01", "DOLL0001", "RELAY001", "short", "FRESH001"]);
        var key = ProbePlan.PickUnpairedKey("ownkey01", ["PARTNER1", "pend-0001", "REMOVE01", "DOLL0001", "RELAY001", null], () => queue.Dequeue());
        Assert.Equal("FRESH001", key);

        Assert.Null(ProbePlan.PickUnpairedKey("OWNKEY01", ["PARTNER1"], () => "PARTNER1", attempts: 5));
        for (var i = 0; i < 50; i++)
        {
            var random = ProbePlan.RandomKey();
            Assert.Equal(8, random.Length);
            Assert.True(PairingKeyUtil.IsValid(random));
        }
    }

    [Fact]
    public void PresencePartners_AreAtMost10_DistinctAndNeverOurs()
    {
        var partners = Enumerable.Range(0, 15).Select(i => $"PART{i:0000}").Append("OWNKEY01").Append("PART0001").ToList();
        var picked = ProbePlan.PresencePartners("OWNKEY01", ["OWNKEY01", .. partners]);
        Assert.Equal(ProbePlan.MaxPresencePartners, picked.Count);
        Assert.DoesNotContain("OWNKEY01", picked);
        Assert.Equal(picked.Count, picked.Distinct().Count());
        Assert.True(ProbePlan.RateBudget <= 13);
    }

    [Fact]
    public void TheStatsUrl_FollowsTheRelayUrl()
    {
        Assert.Equal("https://a.example/stats", ProbePlan.StatsUri("wss://a.example/ws")!.ToString());
        Assert.Equal("http://127.0.0.1:29417/stats", ProbePlan.StatsUri("ws://127.0.0.1:29417/ws")!.ToString());
        Assert.Null(ProbePlan.StatsUri("https://a.example/ws"));
        Assert.Null(ProbePlan.StatsUri(null));
    }

    [Fact]
    public void TheBudget_NeverGoesOver()
    {
        var budget = new SelfTestBudget(3);
        Assert.True(budget.TryTake(2));
        Assert.False(budget.TryTake(2));
        Assert.True(budget.TryTake());
        Assert.False(budget.TryTake());
        Assert.Equal(3, budget.Used);
    }

    // ---------- tap (04-D6) ----------

    private static Envelope Error(string? requestId, string code = ErrorCodes.NotAllowed, string? to = "OWNKEY01") =>
        Envelope.Create(MessageTypes.Error, new ErrorPayload { RequestId = requestId, Code = code, To = to });

    private static Envelope Presence(string requestId, string from, bool online = false, bool? stillPaired = null) =>
        Envelope.Create(MessageTypes.PresenceResult, new PresenceResultPayload { RequestId = requestId, From = from, To = "OWNKEY01", Online = online, StillPaired = stillPaired });

    [Fact]
    public async Task TheTap_ConsumesOnlyItsOwnIds()
    {
        var tap = new SelfTestTap();
        var id = SelfTestTap.NewRequestId();
        Assert.StartsWith("st-", id);
        Assert.Equal(35, id.Length);
        var wait = tap.ExpectAsync(id, Wait, CancellationToken.None);

        Assert.Equal(TapVerdict.None, tap.Inspect(Error(Guid.NewGuid().ToString("N"))));
        Assert.Equal(TapVerdict.None, tap.Inspect(Error(null)));
        Assert.Equal(TapVerdict.None, tap.Inspect(Presence(Guid.NewGuid().ToString("N"), "PARTNER1")));
        Assert.Equal(TapVerdict.None, tap.Inspect(Envelope.Create(MessageTypes.Wind, new WindPayload { RequestId = id })));
        Assert.False(wait.IsCompleted);

        Assert.Equal(TapVerdict.Consume, tap.Inspect(Error(id)));
        var reply = await wait.WaitAsync(Wait);
        Assert.Equal(MessageTypes.Error, reply!.Type);

        // A late or unknown self-test id is still consumed, never handled normally.
        Assert.Equal(TapVerdict.Consume, tap.Inspect(Error(id)));
        Assert.Equal(TapVerdict.Consume, tap.Inspect(Error(SelfTestTap.NewRequestId())));
    }

    [Fact]
    public async Task TheTap_ConsumesAPong_OnlyWhileItsPingIsOutstanding()
    {
        var tap = new SelfTestTap();
        var pong = Envelope.Create(MessageTypes.Pong, new { });
        Assert.Equal(TapVerdict.None, tap.Inspect(pong));

        var wait = tap.ExpectPongAsync(Wait, CancellationToken.None);
        Assert.Equal(TapVerdict.Consume, tap.Inspect(pong));
        Assert.NotNull(await wait.WaitAsync(Wait));
        Assert.Equal(TapVerdict.None, tap.Inspect(pong));

        // A ping that timed out doesn't own a later pong.
        Assert.Null(await tap.ExpectPongAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None));
        Assert.Equal(TapVerdict.None, tap.Inspect(pong));
    }

    [Fact]
    public async Task APartnersPresence_IsObserved_NotConsumed()
    {
        var tap = new SelfTestTap();
        var partnerQuery = SelfTestTap.NewRequestId();
        var randomQuery = SelfTestTap.NewRequestId();
        var partnerWait = tap.ExpectAsync(partnerQuery, Wait, CancellationToken.None, ["partner1"]);
        var randomWait = tap.ExpectAsync(randomQuery, Wait, CancellationToken.None);

        Assert.Equal(TapVerdict.Observe, tap.Inspect(Presence(partnerQuery, "PARTNER1", online: true)));
        Assert.Equal(TapVerdict.Observe, tap.Inspect(Presence(partnerQuery, "PARTNER1", online: true, stillPaired: false)));
        Assert.Equal(TapVerdict.Consume, tap.Inspect(Presence(randomQuery, "RANDOM01")));
        // The partner's id answered "from" someone else is the self-test's own: consumed.
        Assert.Equal(TapVerdict.Consume, tap.Inspect(Presence(partnerQuery, "SOMEONE1")));
        Assert.True((await partnerWait.WaitAsync(Wait))!.GetPayload<PresenceResultPayload>()!.Online);
        Assert.NotNull(await randomWait.WaitAsync(Wait));
    }

    [Fact]
    public void WithoutARun_OnlyALateSelfTestError_IsConsumed()
    {
        Assert.Equal(TapVerdict.Consume, SelfTestTap.Inspect(null, Error(SelfTestTap.NewRequestId())));
        Assert.Equal(TapVerdict.None, SelfTestTap.Inspect(null, Error(Guid.NewGuid().ToString("N"))));
        Assert.Equal(TapVerdict.None, SelfTestTap.Inspect(null, Presence(SelfTestTap.NewRequestId(), "PARTNER1")));
        Assert.Equal(TapVerdict.None, SelfTestTap.Inspect(null, Envelope.Create(MessageTypes.Pong, new { })));
    }

    [Fact]
    public async Task TheTap_TimesOutEachRequestOnItsOwn()
    {
        var tap = new SelfTestTap();
        var slow = tap.ExpectAsync(SelfTestTap.NewRequestId(), Wait, CancellationToken.None);
        var fast = tap.ExpectAsync(SelfTestTap.NewRequestId(), TimeSpan.FromMilliseconds(20), CancellationToken.None);
        Assert.Null(await fast.WaitAsync(Wait));
        Assert.False(slow.IsCompleted);
        tap.CancelAll();
        Assert.Null(await slow.WaitAsync(Wait));
    }

    // ---------- redaction and report (04-D3) ----------

    [Fact]
    public void Redaction_ShowsKeysAsTwoCharactersAndStars_HostsAsNames()
    {
        Assert.Equal("AB******", SelfTestRedact.Key("ABCD1234"));
        Assert.Equal("(none)", SelfTestRedact.Key(""));
        Assert.Equal("windup-relay-oracle.ancon-universe.ts.net", SelfTestRedact.Host("wss://windup-relay-oracle.ancon-universe.ts.net/ws?token=x"));
        Assert.Equal("(invalid-url)", SelfTestRedact.Host("not a url"));
    }

    [Fact]
    public void LogRedact_ShowsKeysAndNamesAsTwoCharactersAndStars()
    {
        // dalamud.log lines (04-D10): never a full key, ContentId or Name@World; the world is kept.
        Assert.Equal("AB******", LogRedact.Key(" ABCD1234 "));
        Assert.Equal("00******", LogRedact.Key("004000174AA8BCC2"));
        Assert.Equal("A******", LogRedact.Key("A"));
        Assert.Equal("(none)", LogRedact.Key(null));
        Assert.Equal("Fi******@Cactuar", LogRedact.Identity("Firstname Lastname@Cactuar"));
        Assert.Equal("Fi******", LogRedact.Identity("Firstname Lastname"));
        Assert.Equal("Fi******", LogRedact.Identity("Firstname Lastname@"));
        Assert.Equal("(none)", LogRedact.Identity(" "));
        Assert.DoesNotContain("Lastname", LogRedact.Identity("Firstname Lastname@Cactuar"));
        Assert.Equal(LogRedact.Key("ABCD1234"), SelfTestRedact.Key("ABCD1234"));
    }

    [Fact]
    public void TheScrubber_ReplacesEverySensitiveValue_WhateverItsCase()
    {
        var secret = RelaySecret.Generate();
        var hash = RelaySecret.HashHex(secret)!;
        const string contentId = "004000174AA8BCC2";
        var text = $"secret {secret} hash {hash.ToUpperInvariant()} cid {contentId.ToLowerInvariant()} key ABCD1234 and ABCD12345 x";
        var scrubbed = SelfTestRedact.Scrub(text, [secret, hash, contentId, "ABCD1234", "ABCD12345", null, "", "ab"]);
        Assert.DoesNotContain(secret, scrubbed);
        Assert.DoesNotContain(hash, scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(contentId, scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ABCD1234", scrubbed);
        Assert.DoesNotContain("]5", scrubbed); // the longer key is replaced whole, not around the shorter one
        Assert.Equal(5, scrubbed.Split(SelfTestRedact.Redacted).Length - 1);
        // Too short to scrub without shredding text.
        Assert.Equal("Self-test", SelfTestRedact.Scrub("Self-test", ["te"]));
    }

    [Fact]
    public void TheReport_IsScrubbed_AndListsFailWarnSkip()
    {
        var secret = RelaySecret.Generate();
        CheckResult[] results =
        [
            CheckResult.Pass("01-12", "ok"),
            CheckResult.Fail("02-5", $"leak {secret}"),
            CheckResult.Warn("X-clock", "skew"),
            CheckResult.Skip("02-2", "no partners"),
            CheckResult.Manual("01-3", "see manual"),
            CheckResult.Info("X-pending", "counts"),
            CheckResult.LoudFail("X-auth-own-random", "took over"),
        ];
        var lines = SelfTestReport.ChatLines("Self-test", results, "20261013T000000Z.log", [secret]);
        Assert.Equal("Self-test: 1 PASS · 2 FAIL · 1 WARN · 1 SKIP · 1 MANUAL — log: selftest/20261013T000000Z.log", lines[0]);
        Assert.Equal(5, lines.Count);
        Assert.StartsWith("FAIL X-auth-own-random", lines[1]);
        Assert.All(lines, l => Assert.DoesNotContain(secret, l));
        var log = SelfTestReport.LogText("Self-test", DateTime.UtcNow, DateTime.UtcNow, "1.0", results, [secret], false);
        Assert.DoesNotContain(secret, log);
        Assert.Contains("INFO X-pending", log);
        Assert.Contains("Manual steps:", log);
    }

    [Fact]
    public void TheLogFolder_KeepsTheNewest5()
    {
        var dir = Path.Combine(Path.GetTempPath(), "windup-selftest-" + Guid.NewGuid().ToString("N"));
        try
        {
            for (var i = 0; i < 7; i++)
                Assert.NotNull(SelfTestReport.WriteLog(dir, SelfTestReport.LogFileName(new DateTime(2026, 10, 13, 0, 0, i, DateTimeKind.Utc)), "x"));
            var kept = Directory.GetFiles(Path.Combine(dir, SelfTestReport.LogFolder)).Select(Path.GetFileName).Order().ToList();
            Assert.Equal(5, kept.Count);
            Assert.Equal("20261013T000002Z.log", kept[0]);
            Assert.Equal("20261013T000006Z.log", kept[^1]);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ---------- runner ----------

    [Fact]
    public async Task ARunnerStep_ThatTimesOut_Fails()
    {
        var runner = new SelfTestRunner();
        var sawCancel = new TaskCompletionSource();
        await runner.StepAsync(["A", "B"], TimeSpan.FromMilliseconds(50), async ct =>
        {
            ct.Register(() => sawCancel.TrySetResult());
            await Task.Delay(Timeout.Infinite, CancellationToken.None).WaitAsync(Wait);
            return [];
        }, CancellationToken.None);
        Assert.Equal(["A", "B"], runner.Results.Select(r => r.Id));
        Assert.All(runner.Results, r => Assert.Equal(CheckStatus.Fail, r.Status));
        await sawCancel.Task.WaitAsync(Wait);
    }

    [Fact]
    public async Task ARunnerStep_ThatThrows_FailsWithTheTypeOnly()
    {
        var runner = new SelfTestRunner();
        await runner.StepAsync(["A"], Wait, _ => throw new InvalidOperationException("wss://secret-host/ws"), CancellationToken.None);
        var result = Assert.Single(runner.Results);
        Assert.Equal(CheckStatus.Fail, result.Status);
        Assert.Contains(nameof(InvalidOperationException), result.Reason);
        Assert.DoesNotContain("secret-host", result.Reason);
    }

    [Fact]
    public async Task ACancelledRun_Stops_ButStillRestores()
    {
        var runner = new SelfTestRunner();
        using var cts = new CancellationTokenSource();
        var restored = false;
        var run = runner.RunWithRestoreAsync("V", Wait, async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        }, _ =>
        {
            restored = true;
            return Task.FromResult(true);
        }, Wait, "press Reconnect", cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Wait));
        Assert.True(restored);
        Assert.Empty(runner.Results);
    }

    [Fact]
    public async Task AFailedRestore_IsALoudFail()
    {
        foreach (Func<CancellationToken, Task<bool>> restore in new Func<CancellationToken, Task<bool>>[]
                 {
                     _ => Task.FromResult(false),
                     _ => throw new IOException(),
                     async ct => { await Task.Delay(Timeout.Infinite, ct); return true; },
                 })
        {
            var runner = new SelfTestRunner();
            await runner.RunWithRestoreAsync("V", Wait, _ => Task.FromResult<IReadOnlyList<CheckResult>>([CheckResult.Pass("V", "done")]),
                restore, TimeSpan.FromMilliseconds(100), "press Reconnect", CancellationToken.None);
            Assert.Equal(2, runner.Results.Count);
            var loud = runner.Results[1];
            Assert.True(loud is { Status: CheckStatus.Fail, Loud: true });
            Assert.Contains("press Reconnect", loud.Reason);
        }
    }

    [Fact]
    public void ASessionChange_TurnsSessionDependentResultsIntoSkip()
    {
        var runner = new SelfTestRunner();
        runner.AddRange([CheckResult.Pass("02-5", "refused"), CheckResult.Fail("02-2", "x"), CheckResult.Pass("01-12", "ok"), CheckResult.LoudFail("X-auth-own-random", "took over")]);
        var before = new SessionInvariants(3, true, false, 0);
        var changed = SessionInvariants.Changed(before, before with { AcceptedConnections = 4 });
        Assert.NotNull(changed);
        Assert.Null(SessionInvariants.Changed(before, before));
        Assert.NotNull(SessionInvariants.Changed(before, before with { Open = false }));
        Assert.NotNull(SessionInvariants.Changed(before, before with { AuthBlocked = true }));
        Assert.NotNull(SessionInvariants.Changed(before, before with { SessionReplacedSeen = 1 }));

        runner.ApplyInvariants(changed, SelfTestSuite.SessionDependent, causedByVerb: false);
        var byId = runner.Results.ToDictionary(r => r.Id);
        Assert.Equal(CheckStatus.Skip, byId["02-5"].Status);
        Assert.Equal(CheckStatus.Skip, byId["02-2"].Status);
        Assert.Equal(CheckStatus.Pass, byId["01-12"].Status);
        Assert.True(byId["X-auth-own-random"].Loud); // a takeover is never hidden
        Assert.Equal(CheckStatus.Warn, byId["X-session"].Status);

        var verb = new SelfTestRunner();
        verb.Add(CheckResult.Pass("03-2", "1009"));
        verb.ApplyInvariants(changed, SelfTestSuite.SessionDependent, causedByVerb: true);
        Assert.Equal(CheckStatus.Pass, verb.Results[0].Status);
        Assert.Equal(CheckStatus.Info, verb.Results[1].Status);
    }

    [Fact]
    public void VerbInvariants_WarnOnlyOnWhatTheVerbDoesntCause()
    {
        var before = new SessionInvariants(3, true, false, 0);
        Assert.Null(SessionInvariants.NotCausedByVerb(before, before with { AcceptedConnections = 4, Open = false }));
        Assert.NotNull(SessionInvariants.NotCausedByVerb(before, before with { SessionReplacedSeen = 1 }));
        Assert.NotNull(SessionInvariants.NotCausedByVerb(before, before with { AuthBlocked = true }));

        var reconnected = new SelfTestRunner();
        reconnected.ApplyVerbInvariants(before, before with { AcceptedConnections = 4 });
        Assert.Equal(CheckStatus.Info, Assert.Single(reconnected.Results).Status);

        var same = new SelfTestRunner();
        same.ApplyVerbInvariants(before, before);
        Assert.Equal(CheckStatus.Pass, Assert.Single(same.Results).Status);

        foreach (var after in new[] { before with { AcceptedConnections = 4, SessionReplacedSeen = 1 }, before with { AuthBlocked = true } })
        {
            var replaced = new SelfTestRunner();
            replaced.ApplyVerbInvariants(before, after);
            Assert.Equal(CheckStatus.Warn, Assert.Single(replaced.Results).Status);
        }
    }

    [Fact]
    public void TheLiveStep_FitsTenPartnerQueriesAndTheOtherChecks()
    {
        var t = new SelfTestTimings();
        // Above the relay's 5 s send timeout to a partner (04-D10).
        Assert.True(t.PresenceReply > TimeSpan.FromSeconds(5));
        // Wind, unwind, ping and the random key at one reply each, then every partner.
        Assert.True(t.LiveStep >= t.Reply * 4 + t.PresenceReply * ProbePlan.MaxPresencePartners + t.Reply);
    }

    [Fact]
    public void TheStartMessages_MatchTheStepBudgets()
    {
        var t = new SelfTestTimings();
        // 04-D11: eight safe steps' worth of Step (two for the own-key probes) plus the live step; the outage's step and restore.
        Assert.Equal(t.Step * 8 + t.LiveStep, t.SafeTierBudget);
        Assert.Equal(TimeSpan.FromSeconds(335), t.SafeTierBudget);
        Assert.Equal(TimeSpan.FromSeconds(180), t.VerbBudget(SelfTestTier.Outage));
        Assert.Equal("usually under a minute, at most about 6 minutes", SelfTestReport.Duration(SelfTestTier.Safe, t));
        Assert.Equal("about 2 minutes, at most about 3", SelfTestReport.Duration(SelfTestTier.Outage, t));
        Assert.Equal("usually under a minute, at most about 4 minutes", SelfTestReport.Duration(SelfTestTier.Safe, t with { Step = TimeSpan.FromSeconds(15) }));
        Assert.Null(SelfTestReport.Duration(SelfTestTier.Reconnect, t));
    }

    [Fact]
    public void TheOwnProbeKey_IsTheRegisteredKey_OnlyWhenItIsTheContentIdKey()
    {
        Assert.Equal("OWNKEY01", ProbePlan.OwnProbeKey("OWNKEY01", "OWNKEY01"));
        Assert.Equal("OWNKEY01", ProbePlan.OwnProbeKey("ownkey01", "OWNKEY01"));
        Assert.Null(ProbePlan.OwnProbeKey(null, "OWNKEY01"));
        Assert.Null(ProbePlan.OwnProbeKey("OWNKEY01", null));
        Assert.Null(ProbePlan.OwnProbeKey("OWNKEY01", "OTHERKEY"));
        Assert.Null(ProbePlan.OwnProbeKey("SHORT", "SHORT"));
        Assert.Contains("isn't known", ProbePlan.OwnProbeKeySkipReason(null));
        Assert.Contains("ContentId", ProbePlan.OwnProbeKeySkipReason("OWNKEY01"));
    }

    // ---------- post-patch judge (04-D8) ----------

    [Fact]
    public void ThePostPatchJudge_FailsMissingSignaturesAddressesAndRows_AndSkipsAbsentIpc()
    {
        var facts = new PostPatchFacts
        {
            Signatures = [new SignatureFact("P-hook-rmi-walk", "RMI walk", true), new SignatureFact("P-hook-rmi-fly", "RMI fly", false)],
            Addresses = [new AddressFact("P-addr-use-action", "UseAction", true), new AddressFact("P-addr-set-rotation", "SetRotation", false)],
            Sheets = [new SheetFact("P-data-emote", "Emote", true), new SheetFact("P-data-action", "Action", false), new SheetFact("P-data-territory", "TerritoryType", true)],
            Rows =
            [
                new SheetRowFact("P-data-emote", "Emote", 13, "Sit", true),
                new SheetRowFact("P-data-emote", "Emote", 52, "Sit on Ground", false),
                new SheetRowFact("P-data-territory", "TerritoryType", 339, "district", true, "region", false),
                new SheetRowFact("P-data-general-action", "GeneralAction", 2, "Jump", true),
            ],
            LoopingEmoteRows = 40,
            Ipc = [new IpcFact("P-ipc-lifestream", "Lifestream", false, false), new IpcFact("P-ipc-vnavmesh", "vnavmesh", true, true)],
            TravelReady = false,
        };
        var byId = PostPatchJudge.Judge(facts).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => Assert.Single(g));
        Assert.Equal(CheckStatus.Pass, byId["P-hook-rmi-walk"].Status);
        Assert.Equal(CheckStatus.Fail, byId["P-hook-rmi-fly"].Status);
        Assert.Equal(CheckStatus.Pass, byId["P-addr-use-action"].Status);
        Assert.Equal(CheckStatus.Fail, byId["P-addr-set-rotation"].Status);
        Assert.Equal(CheckStatus.Fail, byId["P-data-emote"].Status);
        Assert.Equal(CheckStatus.Fail, byId["P-data-action"].Status);
        Assert.Equal(CheckStatus.Warn, byId["P-data-territory"].Status);
        Assert.Equal(CheckStatus.Pass, byId["P-data-general-action"].Status);
        Assert.Equal(CheckStatus.Skip, byId["P-ipc-lifestream"].Status);
        Assert.Equal(CheckStatus.Info, byId["P-ipc-vnavmesh"].Status);
        Assert.Contains("not travel ready", byId["P-ipc-vnavmesh"].Reason);
        Assert.Equal(CheckStatus.Info, byId["P-hook-last"].Status);
        Assert.Equal(CheckStatus.Manual, byId["P-ui-selectyesno"].Status);
        Assert.Equal(CheckStatus.Manual, byId["P-ui-doors"].Status);

        var noLooping = PostPatchJudge.Judge(new PostPatchFacts
        {
            Sheets = [new SheetFact("P-data-emote", "Emote", true)],
            Rows = [new SheetRowFact("P-data-emote", "Emote", 13, "Sit", true)],
            LoopingEmoteRows = 0,
        });
        Assert.Equal(CheckStatus.Fail, noLooping.Single(r => r.Id == "P-data-emote").Status);
    }

    // ---------- session facts ----------

    private static RelayIdentityDiagnosis GoodIdentity() => new(
        "WindUpKey.relay-identity.json", true, true, RelayIdentityFileVersion.Current, RelayIdentityStoreStatus.Available,
        true, true, false, RelayIdentityKeyRecord.Matches, 0);

    [Fact]
    public void SessionFacts_JudgeTheIdentityHostAndPendingState()
    {
        var snapshot = new SessionSnapshot
        {
            OwnKey = "OWNKEY01",
            PairingKeyMatchesContentId = true,
            TokenMatchesCompiled = true,
            PartnerKeys = ["PARTNER1", "PARTNER2"],
            OwnedDollKeys = ["PARTNER1", "OWNKEY01"],
            RelayPeerKeys = ["PARTNER1", "PARTNER2"],
            Connected = true,
            ConnectedUrl = "wss://b.example/ws",
            RegisterAccepted = true,
            LastSuccessfulRelayUrl = "wss://b.example/ws",
            CompiledUrls = ["wss://a.example/ws", "wss://b.example/ws"],
            Identity = GoodIdentity(),
            CopyAvailable = true,
            SameValueImportCheck = RelayIdentityImport.Unchanged,
        };
        var byId = SessionFactsJudge.Judge(snapshot).ToDictionary(r => r.Id);
        foreach (var id in new[] { "X-identity-file", "X-identity-entry", "X-identity-backups", "X-pairing-key", "X-relay-token", "X-host", "X-register", "X-stamps", "X-suspend", "01-1", "01-2", "01-4", "01-10", "01-11", "02-3", "02-4" })
            Assert.True(byId[id].Status == CheckStatus.Pass, $"{id}: {byId[id].Status} {byId[id].Reason}");
        Assert.Equal(CheckStatus.Info, byId["X-pending"].Status);
        Assert.DoesNotContain("OWNKEY01", string.Join(" ", byId.Values.Select(r => r.Reason)));

        var bad = SessionFactsJudge.Judge(snapshot with
        {
            TokenMatchesCompiled = false,
            ConnectedUrl = "wss://evil.example/ws",
            ShouldSuspendMovementLocks = true,
            FutureStampCount = 2,
            PendingRemovalKeys = ["PARTNER2"],
            OwnedDollKeys = ["STALE001"],
            RelayPeerKeys = ["PARTNER1"],
            Identity = GoodIdentity() with { BackupCount = 1, PairingKey = RelayIdentityKeyRecord.Absent },
            SameValueImportCheck = RelayIdentityImport.Replace,
        }).ToDictionary(r => r.Id);
        Assert.Equal(CheckStatus.Fail, bad["X-relay-token"].Status);
        Assert.Equal(CheckStatus.Fail, bad["X-host"].Status);
        Assert.Equal(CheckStatus.Fail, bad["X-suspend"].Status);
        Assert.Equal(CheckStatus.Warn, bad["X-stamps"].Status);
        Assert.Equal(CheckStatus.Warn, bad["X-identity-backups"].Status);
        Assert.Equal(CheckStatus.Warn, bad["X-identity-entry"].Status);
        Assert.Equal(CheckStatus.Warn, bad["02-3"].Status);
        Assert.Equal(CheckStatus.Warn, bad["02-4"].Status);
        Assert.Equal(CheckStatus.Pass, bad["01-2"].Status); // the partner being unpaired doesn't count as missing
        Assert.Equal(CheckStatus.Fail, bad["01-4"].Status);
    }

    // ---------- identity store diagnosis (04-D4, values-free) ----------

    [Fact]
    public void TheIdentityStoreDiagnosis_IsValuesFree_AndReportsTheEntry()
    {
        var root = Path.Combine(Path.GetTempPath(), "windup-diag-" + Guid.NewGuid().ToString("N"));
        var configDir = Path.Combine(root, "pluginConfigs", "WindUpKey");
        Directory.CreateDirectory(configDir);
        try
        {
            const string cid = "0040001234ABCDEF";
            var key = PairingKeyUtil.FromContentIdHex(cid);
            var store = new RelayIdentityStore(configDir, "WindUpKey", _ => { });
            var secret = store.EnsureSecret(cid, out _)!;

            var diagnosis = store.Diagnose(cid, key, configDir);
            Assert.Equal("WindUpKey.relay-identity.json", diagnosis.FileName);
            Assert.True(diagnosis.OutsidePluginFolder);
            // Falsifiable (04-D10): against a folder that holds the file, or one above it, it is inside.
            Assert.False(store.Diagnose(cid, key, Path.Combine(root, "pluginConfigs")).OutsidePluginFolder);
            Assert.False(store.Diagnose(cid, key, root + Path.DirectorySeparatorChar).OutsidePluginFolder);
            Assert.True(store.Diagnose(cid, key, Path.Combine(root, "pluginConfigs", "WindUpKey2")).OutsidePluginFolder);
            Assert.True(diagnosis.FileExists);
            Assert.Equal(RelayIdentityFileVersion.Current, diagnosis.Version);
            Assert.Equal(RelayIdentityStoreStatus.Available, diagnosis.Status);
            Assert.True(diagnosis is { HasEntry: true, SecretValid: true, PreviousValid: false });
            Assert.Equal(RelayIdentityKeyRecord.Matches, diagnosis.PairingKey);
            Assert.Equal(0, diagnosis.BackupCount);
            Assert.Equal(RelayIdentityKeyRecord.Differs, store.Diagnose(cid, "OTHERKEY", configDir).PairingKey);
            Assert.DoesNotContain(secret, diagnosis.ToString());
            Assert.DoesNotContain(cid, diagnosis.ToString());
            Assert.False(store.Diagnose("0040009999999999", "X", configDir).HasEntry);

            // An import keeps the old secret as previous: both are "known", a fresh one isn't.
            var imported = RelaySecret.Generate();
            Assert.Equal(RelayIdentityImport.Replace, store.Import(cid, imported));
            Assert.True(store.IsKnownSecret(cid, imported));
            Assert.True(store.IsKnownSecret(cid, secret));
            Assert.False(store.IsKnownSecret(cid, RelaySecret.Generate()));
            Assert.True(store.Diagnose(cid, key, configDir).PreviousValid);

            var sensitive = store.SensitiveValues(cid);
            Assert.Contains(imported, sensitive);
            Assert.Contains(secret, sensitive);
            Assert.Contains(RelaySecret.HashHex(imported)!, sensitive);
            Assert.Contains(RelaySecret.HashHex(secret)!, sensitive);

            File.WriteAllText(Path.Combine(root, "pluginConfigs", "WindUpKey.relay-identity.json.corrupt-20261013T000000Z"), "x");
            Assert.Equal(1, store.Diagnose(cid, key, configDir).BackupCount);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // ---------- the probe protocol over in-memory sockets ----------

    /// <summary>A connector whose sockets are in-memory pairs; each server side runs <paramref name="serve"/>.</summary>
    private sealed class ScriptedConnector(Func<bool, WebSocket, Task> serve, int? refuseWithoutToken = 401) : IProbeConnector
    {
        public int Open;
        public readonly List<Task> Servers = [];

        /// <summary>Connects made while the previous probe's client socket was still open (04-D5: one at a time).</summary>
        public int Overlaps;

        private WebSocket? _lastClient;

        public async Task<ProbeConnection> ConnectAsync(Uri uri, bool withToken, TimeSpan timeout, CancellationToken ct)
        {
            // Like a real connect, this takes a moment, so probes started together would overlap without the gate.
            await Task.Delay(10, ct);
            if (!withToken && refuseWithoutToken is { } status)
                return new ProbeConnection(null, status, "refused");
            var (server, client) = InMemorySockets.CreatePair();
            if (_lastClient is { State: WebSocketState.Open or WebSocketState.CloseSent or WebSocketState.CloseReceived or WebSocketState.Connecting })
                Interlocked.Increment(ref Overlaps);
            _lastClient = client;
            Interlocked.Increment(ref Open);
            Servers.Add(Task.Run(async () =>
            {
                try
                {
                    await serve(withToken, server);
                }
                finally
                {
                    Interlocked.Decrement(ref Open);
                    server.Dispose();
                }
            }));
            return new ProbeConnection(client, 101, null);
        }
    }

    private static async Task<WireMessage> ReadAsync(WebSocket socket) =>
        await new BoundedMessageReader().ReadAsync(socket, 1 << 20, CancellationToken.None).WaitAsync(Wait);

    private static Task SendAsync(WebSocket socket, Envelope envelope) =>
        socket.SendAsync(System.Text.Encoding.UTF8.GetBytes(envelope.Serialize()), WebSocketMessageType.Text, true, CancellationToken.None);

    private static readonly Uri Relay = new("ws://relay.test/ws");

    [Fact]
    public async Task ARegisterProbe_ThatIsAccepted_ClosesAtOnce()
    {
        var closedBy = new TaskCompletionSource<WebSocketCloseStatus?>();
        var connector = new ScriptedConnector(async (_, server) =>
        {
            var register = await ReadAsync(server);
            Assert.Equal(MessageTypes.Register, Envelope.TryParse(register.Text!)!.Type);
            await SendAsync(server, Envelope.Create(MessageTypes.PairSync, new PairSyncPayload()));
            var next = await ReadAsync(server);
            closedBy.TrySetResult(next.Kind == WireMessageKind.Close ? next.CloseStatus : null);
        });
        var probe = new RelayProbe(connector, new SelfTestTimings());
        var outcome = await probe.RegisterAsync(Relay, "RANDOM01", ProbeSecret.Malformed, CancellationToken.None);
        Assert.Equal(ProbeOutcomeKind.Accepted, outcome.Kind);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, await closedBy.Task.WaitAsync(Wait));
    }

    [Fact]
    public async Task ARegisterProbe_ReadsAuthFailed_AndSendsOnlyTheProbeSecret()
    {
        string? sent = null;
        var connector = new ScriptedConnector(async (_, server) =>
        {
            var register = Envelope.TryParse((await ReadAsync(server)).Text!)!.GetPayload<RegisterPayload>()!;
            sent = register.Secret;
            await SendAsync(server, Envelope.Create(MessageTypes.Error, new ErrorPayload { Code = ErrorCodes.AuthFailed }));
            await server.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, ErrorCodes.AuthFailed, CancellationToken.None);
        });
        var outcome = await new RelayProbe(connector, new SelfTestTimings()).RegisterAsync(Relay, "OWNKEY01", ProbeSecret.Malformed, CancellationToken.None);
        Assert.Equal(ProbeOutcomeKind.AuthFailed, outcome.Kind);
        Assert.Equal(ProbeSecret.MalformedValue, sent);
        Assert.Equal(ProbeOutcomeKind.AuthFailed, RelayProbe.ClassifyClose(WebSocketCloseStatus.PolicyViolation, ErrorCodes.AuthFailed).Kind);
        Assert.Equal(ProbeOutcomeKind.Closed, RelayProbe.ClassifyClose(WebSocketCloseStatus.MessageTooBig, WireCloseReasons.MessageTooBig).Kind);
        Assert.Equal(ProbeOutcomeKind.Collision, RelayProbe.ClassifyError(ErrorCodes.PairKeyCollision).Kind);
    }

    [Fact]
    public async Task AProbe_NeverRegistersWithoutASecret()
    {
        var connector = new ScriptedConnector(async (_, server) => await ReadAsync(server));
        var probe = new RelayProbe(connector, new SelfTestTimings());
        await Assert.ThrowsAsync<ArgumentException>(() => probe.RegisterAsync(Relay, "RANDOM01", ProbeSecret.None, CancellationToken.None));
        Assert.Empty(connector.Servers);
    }

    [Fact]
    public async Task TheLimitProbes_ReadTheCloseOrThePong()
    {
        var closing = new ScriptedConnector(async (_, server) =>
        {
            var message = await ReadAsync(server);
            Assert.True(message.Kind == WireMessageKind.Binary || message.Text!.Length == WireLimits.PreRegisterMessageBytes + 1);
            await server.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, WireCloseReasons.MessageTooBig, CancellationToken.None);
        });
        var outcome = await new RelayProbe(closing, new SelfTestTimings()).OversizeBeforeRegisterAsync(Relay, CancellationToken.None);
        Assert.Equal(ProbeOutcomeKind.Closed, outcome.Kind);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, outcome.CloseStatus);
        Assert.Equal(WireCloseReasons.MessageTooBig, outcome.CloseDescription);

        var answering = new ScriptedConnector(async (_, server) =>
        {
            await ReadAsync(server);
            await SendAsync(server, Envelope.Create(MessageTypes.Pong, new { }));
            await ReadAsync(server);
        });
        Assert.Equal(ProbeOutcomeKind.Pong, (await new RelayProbe(answering, new SelfTestTimings()).OversizeBeforeRegisterAsync(Relay, CancellationToken.None)).Kind);

        var silent = new ScriptedConnector(async (_, server) => await ReadAsync(server).ContinueWith(_ => ReadAsync(server)).Unwrap().ContinueWith(_ => { }));
        var quick = new SelfTestTimings { Reply = TimeSpan.FromMilliseconds(100) };
        Assert.Equal(ProbeOutcomeKind.Timeout, (await new RelayProbe(silent, quick).BinaryBeforeRegisterAsync(Relay, CancellationToken.None)).Kind);

        Assert.Equal(WireLimits.PreRegisterMessageBytes + 1, RelayProbe.PaddedPing(WireLimits.PreRegisterMessageBytes + 1).Length);
        Assert.Equal(LiveSessionChecks.PingBytes, RelayProbe.PaddedPing(LiveSessionChecks.PingBytes).Length);
    }

    [Fact]
    public async Task UpgradeProbes_Read401_And101_AndOnlyOneProbeIsEverOpen()
    {
        var connector = new ScriptedConnector(async (_, server) => await ReadAsync(server));
        var probe = new RelayProbe(connector, new SelfTestTimings());
        var refused = await probe.UpgradeAsync(Relay, false, CancellationToken.None);
        Assert.Equal(ProbeOutcomeKind.HttpRefused, refused.Kind);
        Assert.Equal(401, refused.HttpStatus);
        var upgrades = Enumerable.Range(0, 4).Select(_ => probe.UpgradeAsync(Relay, true, CancellationToken.None)).ToList();
        foreach (var upgrade in upgrades)
            Assert.Equal(ProbeOutcomeKind.Upgraded, (await upgrade.WaitAsync(Wait)).Kind);
        await Task.WhenAll(connector.Servers).WaitAsync(Wait);
        Assert.Equal(0, connector.Overlaps);
        Assert.Equal(0, connector.Open);
    }
}
