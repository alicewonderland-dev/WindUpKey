using System.Numerics;
using System.Text.RegularExpressions;
using WindUpKey.Protocol;
using WindUpKey.SelfTest;
using WindUpKey.Services;

namespace WindUpKey.Tests;

/// <summary>
/// Issue 07 Part A: the Call's safety rules. The rules are Dalamud-free (<see cref="CallLockPolicy"/>,
/// <see cref="CallAckGate"/>, <see cref="CallDeadlines"/>, <see cref="PendingCallBook"/>, <see cref="CallStuckDetector"/>,
/// <see cref="CallRequestRules"/>, <see cref="HousingYesnoMatcher"/>, <see cref="CallLogRules"/>); <c>CallTravelService</c>,
/// <c>RelayClient</c> and the windows need Dalamud, so the checks on their wiring read their source.
/// </summary>
public sealed class CallSafetyTests
{
    private sealed class FakeClock
    {
        public long Now;

        public long Get() => Now;
    }

    // ---------- 07-A1: the lock policy ----------

    [Theory]
    [InlineData(CallPhase.Idle)]
    [InlineData(CallPhase.WaitingGates)]
    [InlineData(CallPhase.WaitingAccept)]
    public void LockPolicy_EveryWaitingPhase_KeepsTheLockAndNoMute(CallPhase phase)
    {
        Assert.False(CallLockPolicy.IsTravelPhase(phase));
        Assert.Equal(new CallLockState(false, false), CallLockPolicy.For(phase, landing: false));
        // A landing grace keeps the bypass, never the mute.
        Assert.Equal(new CallLockState(true, false), CallLockPolicy.For(phase, landing: true));
    }

    [Theory]
    [InlineData(CallPhase.ChangingWorld)]
    [InlineData(CallPhase.Teleporting)]
    [InlineData(CallPhase.Pathing)]
    public void LockPolicy_EveryTravelPhase_HasTheBypassAndTheMute(CallPhase phase)
    {
        Assert.True(CallLockPolicy.IsTravelPhase(phase));
        Assert.Equal(new CallLockState(true, true), CallLockPolicy.For(phase, landing: false));
        Assert.Equal(new CallLockState(true, true), CallLockPolicy.For(phase, landing: true));
    }

    [Fact]
    public void LockPolicy_EveryPhaseIsClassified()
    {
        Assert.Equal(
            [CallPhase.ChangingWorld, CallPhase.Teleporting, CallPhase.Pathing],
            Enum.GetValues<CallPhase>().Where(CallLockPolicy.IsTravelPhase));
    }

    [Fact]
    public void LandingGrace_EndsOnLanding()
    {
        var clock = new FakeClock();
        var grace = new CallLandingGrace(clock.Get);
        grace.Begin(airborne: true);
        Assert.True(grace.IsActive);
        clock.Now = 10_000;
        Assert.True(grace.Update(airborne: true));
        Assert.False(grace.Update(airborne: false));
        Assert.False(grace.IsActive);
        // Over for good: taking off again doesn't bring it back.
        Assert.False(grace.Update(airborne: true));
    }

    [Fact]
    public void LandingGrace_EndsAfter60Seconds()
    {
        var clock = new FakeClock { Now = 1_000 };
        var grace = new CallLandingGrace(clock.Get);
        grace.Begin(airborne: true);
        clock.Now = 1_000 + CallLandingGrace.MaxMs - 1;
        Assert.True(grace.Update(airborne: true));
        clock.Now = 1_000 + CallLandingGrace.MaxMs;
        Assert.False(grace.Update(airborne: true));
        Assert.False(grace.IsActive);
        Assert.Equal(60_000, CallLandingGrace.MaxMs);
    }

    [Fact]
    public void LandingGrace_OnTheGround_NeverStarts()
    {
        var grace = new CallLandingGrace(() => 0);
        grace.Begin(airborne: false);
        Assert.False(grace.IsActive);
        Assert.False(grace.Update(airborne: true));
        grace.Begin(airborne: true);
        grace.End();
        Assert.False(grace.IsActive);
    }

    [Fact]
    public void LandingGrace_ReleaseBypass_IsTrueOnceWhenTheGraceEnds()
    {
        // 07-A10: with no Call left, the bypass comes off on the tick she lands ...
        var clock = new FakeClock { Now = 1_000 };
        var grace = new CallLandingGrace(clock.Get);
        grace.Begin(airborne: true);
        clock.Now = 5_000;
        Assert.False(grace.ReleaseBypass(airborne: true));
        Assert.True(grace.IsActive);
        Assert.True(grace.ReleaseBypass(airborne: false));
        Assert.False(grace.IsActive);
        Assert.False(grace.ReleaseBypass(airborne: false));
        Assert.False(grace.ReleaseBypass(airborne: true));

        // ... or once 60 s have passed in the air.
        var timeout = new CallLandingGrace(clock.Get);
        clock.Now = 0;
        timeout.Begin(airborne: true);
        clock.Now = CallLandingGrace.MaxMs - 1;
        Assert.False(timeout.ReleaseBypass(airborne: true));
        clock.Now = CallLandingGrace.MaxMs;
        Assert.True(timeout.ReleaseBypass(airborne: true));
        Assert.False(timeout.ReleaseBypass(airborne: true));

        // No grace, nothing to release (the bypass was never kept).
        var none = new CallLandingGrace(clock.Get);
        Assert.False(none.ReleaseBypass(airborne: false));
        none.Begin(airborne: false);
        Assert.False(none.ReleaseBypass(airborne: false));
    }

    [Fact]
    public void Service_EveryPhaseChange_GoesThroughSetPhase()
    {
        var source = CallTravelSource;
        var writes = Regex.Matches(source, @"\b_phase\s*=(?!=)").Select(m => m.Index).ToList();
        var declaration = source.IndexOf("private CallPhase _phase = CallPhase.Idle;", StringComparison.Ordinal);
        Assert.True(declaration >= 0);
        writes.Remove(declaration + "private CallPhase ".Length);
        var setPhase = source.IndexOf("    private void SetPhase(CallPhase next)", StringComparison.Ordinal);
        var body = ProfileSettingsTests.MethodBody(source, "    private void SetPhase(CallPhase next)");
        Assert.Single(writes);
        Assert.InRange(writes[0], setPhase, setPhase + body.Length);
        Assert.DoesNotMatch(@"\bTravelPhase\b", source);

        Assert.Contains("_acceptRequested = false;", body);
        Assert.Contains("ApplyLockPolicy();", body);
        Assert.Contains("_landing.Begin(IsAirborne());", body);
        var apply = ProfileSettingsTests.MethodBody(source, "    private void ApplyLockPolicy()");
        Assert.Contains("CallLockPolicy.For(_phase, landing)", apply);
        Assert.Contains("_timer.SetCallTravelBypass(state.Bypass);", apply);
        Assert.Contains("_timer.SetCallTravelInputMute(state.Mute);", apply);
    }

    [Fact]
    public void Service_TheBypassAndMuteGoOnOnlyThroughThePolicy()
    {
        var source = CallTravelSource;
        Assert.DoesNotContain("SetCallTravelBypass(true", source);
        Assert.DoesNotContain("SetCallTravelInputMute(true", source);
        Assert.Single(Regex.Matches(source, Regex.Escape("SetCallTravelInputMute(state.")));

        // RequestAsync never turns the bypass on: it only enters WaitingGates.
        var request = ProfileSettingsTests.MethodBody(source, "    private async Task RequestAsync(CallPayload payload, bool localDebug)");
        Assert.DoesNotContain("SetCallTravelBypass", request);
        Assert.Equal(["SetPhase(CallPhase.WaitingGates)"], Regex.Matches(request, @"SetPhase\([^)]*\)").Select(m => m.Value));

        // BeginTravel enters a travel phase before any travel call, so SyncLockState unlocks first.
        var begin = ProfileSettingsTests.MethodBody(source, "    private void BeginTravel()");
        var phase = begin.IndexOf("SetPhase(changeWorld ? CallPhase.ChangingWorld : CallPhase.Teleporting);", StringComparison.Ordinal);
        Assert.True(phase >= 0);
        Assert.True(phase < begin.IndexOf("TryChangeWorld(", StringComparison.Ordinal));
        Assert.True(phase < begin.IndexOf("StartTerritoryOrPath();", StringComparison.Ordinal));
    }

    [Fact]
    public void Service_EveryExit_RestoresTheBypassAndMute()
    {
        var source = CallTravelSource;
        var cancel = ProfileSettingsTests.MethodBody(source, "    private void CancelInternal(bool landingGrace = true)");
        Assert.Contains("SetPhase(CallPhase.Idle);", cancel);
        Assert.Contains("_timer.SetCallTravelBypass(_landing.IsActive);", cancel);
        Assert.Contains("_timer.SetCallTravelInputMute(false);", cancel);
        Assert.Contains("_pending = null;", cancel);
        // Unloading leaves nothing on, not even a landing grace.
        Assert.Contains("CancelInternal(landingGrace: false);", ProfileSettingsTests.MethodBody(source, "    public void Dispose()"));
        // Every finish (arrival, failure, expiry, stuck, cancel, consent, logout) goes through CancelInternal.
        Assert.Contains("CancelInternal();", ProfileSettingsTests.MethodBody(source, "    private async Task FinishAsync(string status, string message)"));

        // The exception path: the failed prompt (out of travel), or CancelInternal if that throws too.
        var tick = ProfileSettingsTests.MethodBody(source, "    public void Tick()");
        var handler = tick[tick.IndexOf("catch (Exception ex)", StringComparison.Ordinal)..];
        Assert.Contains("EnterFailedPrompt(\"Unhandled tick exception.\", ex.GetType().Name);", handler);
        Assert.Contains("CancelInternal();", handler);
        Assert.Contains("SetPhase(CallPhase.WaitingAccept);", ProfileSettingsTests.MethodBody(source, "    private void EnterFailedPrompt("));

        // With no Call left, the landing grace still ends.
        Assert.True(tick.IndexOf("TickLandingGrace();", StringComparison.Ordinal) < tick.IndexOf("try", StringComparison.Ordinal));
        // 07-A10: the release is the unit-tested rule, unconditionally wired.
        Assert.Contains(
            "        if (_landing.ReleaseBypass(IsAirborne()))\n            _timer.SetCallTravelBypass(false);",
            ProfileSettingsTests.MethodBody(source, "    private void TickLandingGrace()"));
    }

    // ---------- 07-A2: acks ----------

    [Fact]
    public void AckGate_AThousandTicksInAnInstance_SendOneAck()
    {
        var clock = new FakeClock();
        var gate = new CallAckGate(clock.Get);
        var sent = 0;
        for (var i = 0; i < 1000; i++)
        {
            clock.Now = i * 16L;
            if (gate.Offer(CallAckStatuses.Instance) is not null)
                sent++;
            if (gate.Flush() is not null)
                sent++;
        }

        Assert.Equal(1, sent);
    }

    [Fact]
    public void AckGate_InstanceTravelingBusy_SendsThree()
    {
        var clock = new FakeClock();
        var gate = new CallAckGate(clock.Get);
        Assert.Equal(CallAckStatuses.Instance, gate.Offer(CallAckStatuses.Instance));
        clock.Now = 5_000;
        Assert.Equal(CallAckStatuses.Traveling, gate.Offer(CallAckStatuses.Traveling));
        clock.Now = 10_000;
        Assert.Equal(CallAckStatuses.Busy, gate.Offer(CallAckStatuses.Busy));
        Assert.Null(gate.Offer(CallAckStatuses.Busy));
    }

    [Fact]
    public void AckGate_AChangeInsideTheFloor_IsHeldAndSentOnceItPasses()
    {
        var clock = new FakeClock();
        var gate = new CallAckGate(clock.Get);
        Assert.NotNull(gate.Offer(CallAckStatuses.Instance));
        clock.Now = 500;
        Assert.Null(gate.Offer(CallAckStatuses.Traveling));
        clock.Now = 900;
        Assert.Null(gate.Offer(CallAckStatuses.Busy));
        clock.Now = CallAckGate.FloorMs - 1;
        Assert.Null(gate.Flush());
        clock.Now = CallAckGate.FloorMs;
        Assert.Equal(CallAckStatuses.Busy, gate.Flush());
        Assert.Null(gate.Flush());

        // Back to what was sent last: nothing is held.
        clock.Now = 3_000;
        Assert.Null(gate.Offer(CallAckStatuses.Combat));
        Assert.Null(gate.Offer(CallAckStatuses.Busy));
        clock.Now = 10_000;
        Assert.Null(gate.Flush());
    }

    [Fact]
    public void AckGate_ANewCall_ResetsIt()
    {
        var clock = new FakeClock();
        var gate = new CallAckGate(clock.Get);
        Assert.NotNull(gate.Offer(CallAckStatuses.Instance));
        gate.Reset();
        Assert.Equal(CallAckStatuses.Instance, gate.Offer(CallAckStatuses.Instance));
    }

    [Fact]
    public void Service_EveryAck_GoesThroughTheGate()
    {
        var source = CallTravelSource;
        var ackCalls = Regex.Matches(source, @"\bAckAsync\(").Count;
        // The definition, the gated helper, the flush, and RequestAsync's awaited first ack.
        Assert.Equal(4, ackCalls);
        Assert.Contains("if (_ackGate.Offer(status) is { } send)\n            Forget(AckAsync(send), \"ack\");", source);
        Assert.Contains("if (_ackGate.Flush() is { } held)\n            Forget(AckAsync(held), \"ack\");", source);
        Assert.Contains("if (_ackGate.Offer(ClassifyGateStatus()) is { } status)\n            await AckAsync(status).ConfigureAwait(false);", source);
        Assert.Contains("_ackGate.Reset();", ProfileSettingsTests.MethodBody(source, "    private async Task RequestAsync(CallPayload payload, bool localDebug)"));
        Assert.Contains("FlushAck();", ProfileSettingsTests.MethodBody(source, "    public void Tick()"));
    }

    [Fact]
    public void RelayBusyNotice_ShowsOncePer30Seconds()
    {
        var clock = new FakeClock();
        var notice = new NoticeThrottle(30_000, clock.Get);
        Assert.True(notice.TryShow());
        clock.Now = 29_999;
        Assert.False(notice.TryShow());
        clock.Now = 30_000;
        Assert.True(notice.TryShow());
        Assert.False(notice.TryShow());

        var relay = RelayClientSource;
        Assert.Contains("private const long RelayBusyNoticeIntervalMs = 30_000;", relay);
        Assert.Contains("if (_relayBusyNotice.TryShow())\n                _notifier.NotifyWinderError(\"Relay is busy; try again shortly.\");", relay);
        Assert.Single(Regex.Matches(relay, Regex.Escape("\"Relay is busy; try again shortly.\"")));
    }

    // ---------- 07-A3: deadlines ----------

    [Fact]
    public void Deadlines_TheWaitLimit_FiresExactlyAt10Minutes()
    {
        var clock = new FakeClock { Now = 7 };
        var deadlines = new CallDeadlines(clock.Get);
        deadlines.Start();
        clock.Now = 7 + CallDeadlines.WaitLimitMs - 1;
        Assert.Equal(CallExpiry.None, deadlines.Check(CallWaitKind.Waiting, betweenAreas: false));
        clock.Now = 7 + CallDeadlines.WaitLimitMs;
        Assert.Equal(CallExpiry.WaitLimit, deadlines.Check(CallWaitKind.Waiting, betweenAreas: false));
        // Travelling isn't waiting.
        Assert.Equal(CallExpiry.None, deadlines.Check(CallWaitKind.Traveling, betweenAreas: false));
        Assert.Equal(10 * 60_000, CallDeadlines.WaitLimitMs);
    }

    [Fact]
    public void Deadlines_TheFailedPromptLimit_FiresExactly3MinutesAfterEachFailure()
    {
        var clock = new FakeClock();
        var deadlines = new CallDeadlines(clock.Get);
        deadlines.Start();
        clock.Now = 60_000;
        deadlines.MarkFailure();
        clock.Now = 60_000 + CallDeadlines.FailedPromptLimitMs - 1;
        Assert.Equal(CallExpiry.None, deadlines.Check(CallWaitKind.FailedPrompt, betweenAreas: false));
        // A second failure starts it over.
        deadlines.MarkFailure();
        var second = clock.Now;
        clock.Now = second + CallDeadlines.FailedPromptLimitMs - 1;
        Assert.Equal(CallExpiry.None, deadlines.Check(CallWaitKind.FailedPrompt, betweenAreas: false));
        clock.Now = second + CallDeadlines.FailedPromptLimitMs;
        Assert.Equal(CallExpiry.FailedPromptLimit, deadlines.Check(CallWaitKind.FailedPrompt, betweenAreas: false));
        Assert.Equal(3 * 60_000, CallDeadlines.FailedPromptLimitMs);
    }

    [Fact]
    public void Deadlines_TheOverallCap_FiresExactlyAt20Minutes_WhateverTheCallIsDoing()
    {
        var clock = new FakeClock();
        var deadlines = new CallDeadlines(clock.Get);
        deadlines.Start();
        clock.Now = CallDeadlines.OverallCapMs - 1;
        Assert.Equal(CallExpiry.None, deadlines.Check(CallWaitKind.Traveling, betweenAreas: false));
        clock.Now = CallDeadlines.OverallCapMs;
        Assert.Equal(CallExpiry.OverallCap, deadlines.Check(CallWaitKind.Traveling, betweenAreas: false));
        deadlines.MarkFailure();
        Assert.Equal(CallExpiry.OverallCap, deadlines.Check(CallWaitKind.FailedPrompt, betweenAreas: false));
        Assert.Equal(20 * 60_000, CallDeadlines.OverallCapMs);
    }

    [Fact]
    public void Deadlines_NothingExpiresBetweenAreas_UpTo2MinutesPastTheDeadline()
    {
        var clock = new FakeClock();
        var deadlines = new CallDeadlines(clock.Get);
        deadlines.Start();
        clock.Now = CallDeadlines.WaitLimitMs;
        Assert.Equal(CallExpiry.None, deadlines.Check(CallWaitKind.Waiting, betweenAreas: true));
        clock.Now = CallDeadlines.WaitLimitMs + CallDeadlines.TransitCeilingMs - 1;
        Assert.Equal(CallExpiry.None, deadlines.Check(CallWaitKind.Waiting, betweenAreas: true));
        clock.Now = CallDeadlines.WaitLimitMs + CallDeadlines.TransitCeilingMs;
        Assert.Equal(CallExpiry.WaitLimit, deadlines.Check(CallWaitKind.Waiting, betweenAreas: true));

        // After the transit, the next check expires it.
        clock.Now = CallDeadlines.WaitLimitMs + 30_000;
        Assert.Equal(CallExpiry.WaitLimit, deadlines.Check(CallWaitKind.Waiting, betweenAreas: false));
        Assert.Equal(2 * 60_000, CallDeadlines.TransitCeilingMs);
    }

    [Fact]
    public void Service_Deadlines_EndTheCallWithAChatLine()
    {
        var source = CallTravelSource;
        var tick = ProfileSettingsTests.MethodBody(source, "    private bool TickDeadlines()");
        Assert.Contains("_deadlines.Check(kind, IsBetweenAreas())", tick);
        Assert.Contains("PluginChat.Print(_chat, CallExpiredEcho, PluginChat.Yellow);", tick);
        Assert.Contains("FinishAsync(CallResultStatuses.Failed, \"Call expired.\")", tick);
        Assert.Contains("private const string CallExpiredEcho = \"Your owner's Call expired.\";", source);
        Assert.Contains("_deadlines.Start();", ProfileSettingsTests.MethodBody(source, "    private async Task RequestAsync(CallPayload payload, bool localDebug)"));
        Assert.Contains("_deadlines.MarkFailure();", ProfileSettingsTests.MethodBody(source, "    private void EnterFailedPrompt("));
    }

    // ---------- 07-A3: the owner's pending book ----------

    [Fact]
    public void PendingBook_ForgetsACallAfter30Minutes()
    {
        var clock = new FakeClock();
        var book = new PendingCallBook(clock.Get);
        book.Add("a");
        book.Add("b");
        clock.Now = PendingCallBook.TtlMs - 1;
        Assert.True(book.TryTake("a"));
        clock.Now = PendingCallBook.TtlMs;
        Assert.False(book.TryTake("b"));
        Assert.Equal(0, book.Count);
        Assert.Equal(30 * 60_000, PendingCallBook.TtlMs);
    }

    [Fact]
    public void PendingBook_KeepsAtMost32_DroppingTheOldest()
    {
        var clock = new FakeClock();
        var book = new PendingCallBook(clock.Get);
        for (var i = 0; i < 40; i++)
        {
            clock.Now = i;
            book.Add($"id{i}");
        }

        Assert.Equal(PendingCallBook.MaxEntries, book.Count);
        Assert.Equal(32, PendingCallBook.MaxEntries);
        for (var i = 0; i < 8; i++)
            Assert.False(book.TryTake($"id{i}"));
        for (var i = 8; i < 40; i++)
            Assert.True(book.TryTake($"id{i}"));
    }

    [Fact]
    public void PendingBook_TakesEachIdOnce()
    {
        var book = new PendingCallBook(() => 0);
        book.Add("a");
        Assert.True(book.TryTake("a"));
        Assert.False(book.TryTake("a"));
        Assert.False(book.TryTake(null));
        Assert.False(book.TryTake(string.Empty));
        Assert.False(book.TryTake("never"));
    }

    [Fact]
    public void PendingBook_IsThreadSafe()
    {
        var book = new PendingCallBook(() => 0);
        Parallel.For(0, 2_000, i =>
        {
            book.Add($"id{i}");
            book.TryTake($"id{i - 1}");
        });
        Assert.InRange(book.Count, 0, PendingCallBook.MaxEntries);
    }

    [Fact]
    public void RelayClient_UsesThePendingBook()
    {
        var relay = RelayClientSource;
        Assert.Contains("private readonly PendingCallBook _pendingCalls = new(() => Environment.TickCount64);", relay);
        Assert.Contains("_pendingCalls.Add(requestId);", ProfileSettingsTests.MethodBody(relay, "    public async Task SendCallAsync(string dollKey)"));
        Assert.Contains("private bool TryTakePendingCall(string? requestId) => _pendingCalls.TryTake(requestId);", relay);
        Assert.DoesNotContain("_pendingCallRequestIds", relay);
    }

    // ---------- 07-A4 / 07-D14: stuck detection ----------

    private static readonly Vector3 Target = new(100, 0, 100);
    private static readonly Vector3 Waypoint = new(60, 0, 60);

    /// <summary>One tick towards the final target itself (no waypoint list), a path running.</summary>
    private static CallStuckReason ToTarget(CallStuckDetector stuck, float distance, bool pathRunning = true, bool pathfinding = false, bool paused = false) =>
        stuck.Observe(Target, Target, 0, true, distance, false, pathRunning, pathfinding, paused);

    [Fact]
    public void Stuck_NoProgress_FiresAt10Seconds()
    {
        var clock = new FakeClock();
        var stuck = new CallStuckDetector(clock.Get);
        Assert.Equal(CallStuckReason.None, ToTarget(stuck, 50));
        for (clock.Now = 100; clock.Now < CallStuckDetector.NoProgressMs; clock.Now += 100)
            Assert.Equal(CallStuckReason.None, ToTarget(stuck, 48.1f));
        clock.Now = CallStuckDetector.NoProgressMs;
        Assert.Equal(CallStuckReason.NoProgress, ToTarget(stuck, 48.1f));
        Assert.Equal(10_000, CallStuckDetector.NoProgressMs);
        Assert.Equal(2f, CallStuckDetector.MinProgressYalms);
    }

    [Fact]
    public void Stuck_WallRunning_IsStuckWithin10Seconds()
    {
        // 07-D14: running into a wall. She jitters and slides sideways, away and back, along a running path to the
        // same waypoint, but never gets 2 yalms closer to it than her best (10): stuck at 10 s.
        var clock = new FakeClock();
        var stuck = new CallStuckDetector(clock.Get);
        Assert.Equal(CallStuckReason.None, stuck.Observe(Target, Waypoint, 6, true, 10f, false, true, false, false));
        var i = 0;
        for (clock.Now = 100; clock.Now < CallStuckDetector.NoProgressMs; clock.Now += 100)
        {
            // 8.1 .. 12.9 yalms: sliding away along the wall and back, with a little jitter.
            var distance = 10.5f + 2.3f * MathF.Sin(++i * 0.37f) + (i % 2 == 0 ? 0.1f : -0.1f);
            Assert.InRange(distance, 8.05f, 13f);
            Assert.Equal(CallStuckReason.None, stuck.Observe(Target, Waypoint, 6, true, distance, false, true, false, false));
        }

        clock.Now = CallStuckDetector.NoProgressMs;
        Assert.Equal(CallStuckReason.NoProgress, stuck.Observe(Target, Waypoint, 6, true, 12.5f, false, true, false, false));
    }

    [Fact]
    public void Stuck_TheProgressDistance_IsXzOnTheGround_And3dInTheAir()
    {
        var node = new Vector3(10, 0, 0);
        // Bobbing on stairs or a slope isn't headway on the ground.
        Assert.Equal(10f, CallStuckDetector.ProgressDistance(new Vector3(0, 0, 0), node, airborne: false));
        Assert.Equal(10f, CallStuckDetector.ProgressDistance(new Vector3(0, 7, 0), node, airborne: false));
        Assert.Equal(5f, CallStuckDetector.ProgressDistance(new Vector3(7, 4, 4), node, airborne: false));
        // Flying or diving, height counts.
        Assert.Equal(MathF.Sqrt(149f), CallStuckDetector.ProgressDistance(new Vector3(0, 7, 0), node, airborne: true), 4);
    }

    [Fact]
    public void Stuck_RealProgress_ResetsIt()
    {
        var clock = new FakeClock();
        var stuck = new CallStuckDetector(clock.Get);
        ToTarget(stuck, 50);
        clock.Now = 7_000;
        Assert.Equal(CallStuckReason.None, ToTarget(stuck, 48));
        // Measured from the new best (48): 46.5 is no headway, nor is a step back.
        clock.Now = 12_000;
        Assert.Equal(CallStuckReason.None, ToTarget(stuck, 52));
        clock.Now = 16_999;
        Assert.Equal(CallStuckReason.None, ToTarget(stuck, 46.5f));
        clock.Now = 17_000;
        Assert.Equal(CallStuckReason.NoProgress, ToTarget(stuck, 46.5f));
    }

    [Fact]
    public void Stuck_ANewWaypoint_CountsAsProgress_ARepathDoesnt()
    {
        var clock = new FakeClock();
        var stuck = new CallStuckDetector(clock.Get);
        var next = Waypoint + new Vector3(20, 0, 0);
        stuck.Observe(Target, Waypoint, 6, true, 10, false, true, false, false);
        clock.Now = 8_000;
        // vnav reached the waypoint and heads for the next one, further away: that is progress.
        Assert.Equal(CallStuckReason.None, stuck.Observe(Target, next, 5, true, 25, false, true, false, false));
        clock.Now = 17_999;
        Assert.Equal(CallStuckReason.None, stuck.Observe(Target, next, 5, true, 24, false, true, false, false));
        clock.Now = 18_000;
        Assert.Equal(CallStuckReason.NoProgress, stuck.Observe(Target, next, 5, true, 24, false, true, false, false));

        // A re-path to another first node with no fewer waypoints sets the best afresh, but isn't progress.
        var repath = new CallStuckDetector(clock.Get);
        clock.Now = 0;
        repath.Observe(Target, Waypoint, 6, true, 10, false, true, false, false);
        clock.Now = 8_000;
        Assert.Equal(CallStuckReason.None, repath.Observe(Target, next, 7, true, 30, false, true, false, false));
        clock.Now = 10_000;
        Assert.Equal(CallStuckReason.NoProgress, repath.Observe(Target, next, 7, true, 29, false, true, false, false));

        // ... and is measured from there: 2 yalms closer to the new node is progress.
        var closer = new CallStuckDetector(clock.Get);
        clock.Now = 0;
        closer.Observe(Target, Waypoint, 6, true, 10, false, true, false, false);
        clock.Now = 8_000;
        closer.Observe(Target, next, 7, true, 30, false, true, false, false);
        clock.Now = 9_000;
        Assert.Equal(CallStuckReason.None, closer.Observe(Target, next, 7, true, 28, false, true, false, false));
        clock.Now = 18_999;
        Assert.Equal(CallStuckReason.None, closer.Observe(Target, next, 7, true, 28, false, true, false, false));
        clock.Now = 19_000;
        Assert.Equal(CallStuckReason.NoProgress, closer.Observe(Target, next, 7, true, 28, false, true, false, false));

        // Fewer waypoints with no path running (it stopped, and a retry starts a shorter one) isn't progress.
        var stopped = new CallStuckDetector(clock.Get);
        for (clock.Now = 0; clock.Now < 8_000; clock.Now += 1_000)
            stopped.Observe(Target, Waypoint, 6, true, 10, false, true, false, false);
        clock.Now = 8_000;
        Assert.Equal(CallStuckReason.None, stopped.Observe(Target, next, 3, true, 25, false, false, false, false));
        clock.Now = 9_000;
        Assert.Equal(CallStuckReason.None, stopped.Observe(Target, next, 3, true, 25, false, true, false, false));
        clock.Now = 10_000;
        Assert.Equal(CallStuckReason.NoProgress, stopped.Observe(Target, next, 3, true, 25, false, true, false, false));
    }

    [Fact]
    public void Stuck_ANewTarget_ResetsIt()
    {
        var clock = new FakeClock();
        var stuck = new CallStuckDetector(clock.Get);
        var moved = Target + new Vector3(5, 0, 0);
        ToTarget(stuck, 50);
        clock.Now = 9_000;
        ToTarget(stuck, 50);
        Assert.Equal(CallStuckReason.None, stuck.Observe(moved, moved, 0, true, 50, false, true, false, false));
        clock.Now = 15_000;
        Assert.Equal(CallStuckReason.None, stuck.Observe(moved, moved, 0, true, 50, false, true, false, false));
        // A target that only jitters (up to 1 yalm) is the same one.
        var jittered = Target + new Vector3(5.9f, 0, 0);
        clock.Now = 19_000;
        Assert.Equal(CallStuckReason.NoProgress, stuck.Observe(jittered, jittered, 0, true, 50, false, true, false, false));
    }

    [Fact]
    public void Stuck_WhilePausedOrZoning_TimeDoesntCount()
    {
        var clock = new FakeClock();
        var stuck = new CallStuckDetector(clock.Get);
        ToTarget(stuck, 50);
        clock.Now = 5_000;
        ToTarget(stuck, 50);
        for (clock.Now = 5_000; clock.Now <= 60_000; clock.Now += 1_000)
            Assert.Equal(CallStuckReason.None, ToTarget(stuck, 50, pathRunning: false, paused: true));
        // A travel tick that isn't pathing pauses it too.
        clock.Now = 90_000;
        stuck.Pause();
        clock.Now = 94_999;
        Assert.Equal(CallStuckReason.None, ToTarget(stuck, 50));
        clock.Now = 95_000;
        Assert.Equal(CallStuckReason.NoProgress, ToTarget(stuck, 50));
    }

    [Fact]
    public void Stuck_TheWindowHolds_WhileVnavIsStillPathfinding()
    {
        // 07-A11: a long pathfind (no path running yet) isn't a stall; the can't-path check waits for it too.
        var clock = new FakeClock();
        var stuck = new CallStuckDetector(clock.Get);
        ToTarget(stuck, 50, pathRunning: false, pathfinding: true);
        for (clock.Now = 500; clock.Now <= 30_000; clock.Now += 500)
            Assert.Equal(CallStuckReason.None, ToTarget(stuck, 50, pathRunning: false, pathfinding: true));
        // The path runs, and goes nowhere: 10 s from here.
        for (clock.Now = 30_500; clock.Now < 40_000; clock.Now += 500)
            Assert.Equal(CallStuckReason.None, ToTarget(stuck, 50));
        clock.Now = 40_000;
        Assert.Equal(CallStuckReason.NoProgress, ToTarget(stuck, 50));

        // A pathfind while a path is running (a re-plan) doesn't hold it.
        var replanning = new CallStuckDetector(clock.Get);
        clock.Now = 0;
        ToTarget(replanning, 50, pathfinding: true);
        clock.Now = CallStuckDetector.NoProgressMs;
        Assert.Equal(CallStuckReason.NoProgress, ToTarget(replanning, 50, pathfinding: true));
    }

    [Fact]
    public void Stuck_NoPathRunning_FiresAt5Seconds_UnlessStillPathfinding()
    {
        var clock = new FakeClock();
        var stuck = new CallStuckDetector(clock.Get);
        ToTarget(stuck, 50, pathRunning: false);
        clock.Now = 1;
        Assert.Equal(CallStuckReason.None, ToTarget(stuck, 50, pathRunning: false));
        clock.Now = 1 + CallStuckDetector.CannotPathMs - 1;
        Assert.Equal(CallStuckReason.None, ToTarget(stuck, 50, pathRunning: false));
        clock.Now = 1 + CallStuckDetector.CannotPathMs;
        Assert.Equal(CallStuckReason.None, ToTarget(stuck, 50, pathRunning: false, pathfinding: true));
        Assert.Equal(CallStuckReason.CannotPath, ToTarget(stuck, 50, pathRunning: false));

        // A running path holds it off.
        var running = new CallStuckDetector(clock.Get);
        clock.Now = 0;
        ToTarget(running, 50);
        clock.Now = CallStuckDetector.NoProgressMs - 1;
        Assert.Equal(CallStuckReason.None, ToTarget(running, 50));
    }

    [Fact]
    public void Stuck_AWaypointFallbackFlip_KeepsTheCantPathClock_AndIsntProgress()
    {
        // 07-A11: with no path running, the waypoint list empties and the node falls back to the final target; a brief
        // retry fills it again. Flipping between the two must not restart the can't-path count.
        var clock = new FakeClock();
        var stuck = new CallStuckDetector(clock.Get);
        stuck.Observe(Target, Waypoint, 3, true, 10, false, false, false, false);
        // The count starts on the first tick after the reset (at 250).
        for (clock.Now = 250; clock.Now < 250 + CallStuckDetector.CannotPathMs; clock.Now += 250)
        {
            var onWaypoint = clock.Now / 250 % 2 == 0;
            Assert.Equal(
                CallStuckReason.None,
                stuck.Observe(Target, onWaypoint ? Waypoint : Target, onWaypoint ? 3 : 0, true, onWaypoint ? 10 : 50, false, false, false, false));
        }

        clock.Now = 250 + CallStuckDetector.CannotPathMs;
        Assert.Equal(CallStuckReason.CannotPath, stuck.Observe(Target, Target, 0, true, 50, false, false, false, false));

        // With a path running, flips (a nearer node, then a farther one) never count as headway.
        var running = new CallStuckDetector(clock.Get);
        clock.Now = 0;
        running.Observe(Target, Target, 0, true, 50, false, true, false, false);
        for (clock.Now = 250; clock.Now < CallStuckDetector.NoProgressMs; clock.Now += 250)
        {
            var onWaypoint = clock.Now / 250 % 2 == 0;
            Assert.Equal(
                CallStuckReason.None,
                running.Observe(Target, onWaypoint ? Waypoint : Target, onWaypoint ? 3 : 0, true, onWaypoint ? 10 : 50, false, true, false, false));
        }

        clock.Now = CallStuckDetector.NoProgressMs;
        Assert.Equal(CallStuckReason.NoProgress, running.Observe(Target, Waypoint, 3, true, 10, false, true, false, false));
    }

    [Fact]
    public void Service_Stuck_StopsOurTravelWithAChatLine_InHardcoreToo()
    {
        var source = CallTravelSource;
        var tick = ProfileSettingsTests.MethodBody(source, "    private bool TickStuckDetection()");
        Assert.Contains("var node = _waypoints.Read(target, out var waypointCount, out var waypointsAvailable);", tick);
        // 07-A15: the same airborne reading picks 3D or XZ and tells the detector about a take-off or landing.
        Assert.Contains("var airborne = IsAirborne();", tick);
        Assert.Contains("var distance = CallStuckDetector.ProgressDistance(position, node, airborne);", tick);
        Assert.Contains("_stuck.Observe(\n            target,\n            node,\n            waypointCount,\n            waypointsAvailable,\n            distance,\n            airborne,\n            IsVnavPathRunning(),\n            IsVnavPathfindPending(),\n            paused);", tick);
        Assert.Contains("IsBetweenAreas()", tick);
        Assert.Contains("IsMountTransition()", tick);
        Assert.Contains("IsPlayerCasting()", tick);
        // The final target, not the waypoint, drives the door pause.
        Assert.Contains("(_phase == CallPhase.Teleporting && Vector3.Distance(position, target) <= HouseEntranceInteractYalms)", tick);
        Assert.Contains("if (_weOwnTravel)\n            AbortCallAutomation();", tick);
        // The door approach of an indoor Call counts only while it is pathing; any other travel step pauses it.
        Assert.Contains("_phase == CallPhase.Teleporting && _doorApproachActive && _houseEntranceDest is { } door", tick);
        Assert.Contains("_stuck.Pause();", tick);
        Assert.Contains("ResetStuckDetection(newCall: false);", tick);
        Assert.Contains("_stuck.Reset();", ProfileSettingsTests.MethodBody(source, "    private void ResetStuckDetection(bool newCall)"));
        Assert.Contains("PluginChat.Print(_chat, CallStuckEcho, PluginChat.Yellow);", tick);
        Assert.Contains("FinishAsync(CallResultStatuses.Failed, \"Couldn't reach the destination.\")", tick);
        Assert.DoesNotContain("Hardcore", tick);
        Assert.Contains("private const string CallStuckEcho = \"Couldn't reach your owner; the Call was stopped.\";", source);
        // The 3-minute step timeout stays.
        Assert.Contains("private static readonly TimeSpan TravelStepTimeout = TimeSpan.FromMinutes(3);", source);
    }

    /// <summary>A fake vnav waypoint IPC for <see cref="CallWaypointReader"/>: counts its calls, and can throw.</summary>
    private sealed class FakeWaypointIpc
    {
        public int? Count = 6;
        public List<Vector3> List = [Waypoint, Target];
        public Exception? CountThrows;
        public Exception? ListThrows;
        public int CountCalls;
        public int ListCalls;

        public CallWaypointReader Reader() => new(
            () =>
            {
                CountCalls++;
                return CountThrows is null ? Count : throw CountThrows;
            },
            () =>
            {
                ListCalls++;
                return ListThrows is null ? List : throw ListThrows;
            },
            ex => ex is NotSupportedException);
    }

    [Fact]
    public void Waypoints_TheCountIsPolledEachTick_TheListOnlyWhenItChanges()
    {
        // 07-A11: the list allocates, so it is read only when the count changes; its first entry is the node.
        var ipc = new FakeWaypointIpc();
        var reader = ipc.Reader();
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(Waypoint, reader.Read(Target, out var count, out var available));
            Assert.Equal(6, count);
            Assert.True(available);
        }

        Assert.Equal(5, ipc.CountCalls);
        Assert.Equal(1, ipc.ListCalls);
        var next = Waypoint + new Vector3(20, 0, 0);
        ipc.Count = 5;
        ipc.List = [next, Target];
        Assert.Equal(next, reader.Read(Target, out _, out _));
        Assert.Equal(2, ipc.ListCalls);

        // No path: the final target, with nothing to list.
        ipc.Count = 0;
        Assert.Equal(Target, reader.Read(Target, out var none, out var noneAvailable));
        Assert.Equal(0, none);
        Assert.True(noneAvailable);
        Assert.Equal(2, ipc.ListCalls);

        // A waiting phase or a cancel reads the list afresh.
        ipc.Count = 5;
        reader.Read(Target, out _, out _);
        reader.Reset(newCall: false);
        reader.Read(Target, out _, out _);
        Assert.Equal(4, ipc.ListCalls);
    }

    [Fact]
    public void Waypoints_AMissingIpc_IsRememberedUntilTheNextCall_AndTheyAreUnavailable()
    {
        // 07-A15: an older vnavmesh throws "not ready" for the waypoint IPC. It isn't called again every tick, the
        // waypoints are unavailable (the no-progress check is off), and a waiting phase doesn't forget it; a new Call does.
        var ipc = new FakeWaypointIpc { CountThrows = new NotSupportedException() };
        var reader = ipc.Reader();
        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(Target, reader.Read(Target, out var count, out var available));
            Assert.Equal(0, count);
            Assert.False(available);
        }

        Assert.True(reader.Missing);
        Assert.Equal(1, ipc.CountCalls);
        Assert.Equal(0, ipc.ListCalls);
        reader.Reset(newCall: false);
        reader.Read(Target, out _, out _);
        Assert.Equal(1, ipc.CountCalls);

        // The next Call tries again (vnavmesh may have been updated).
        ipc.CountThrows = null;
        reader.Reset(newCall: true);
        Assert.False(reader.Missing);
        Assert.Equal(Waypoint, reader.Read(Target, out _, out var availableAgain));
        Assert.True(availableAgain);
        Assert.Equal(2, ipc.CountCalls);

        // No IPC bound at all is missing too, and so is a list that is "not ready".
        var unbound = new CallWaypointReader(() => null, () => null, _ => false);
        unbound.Read(Target, out _, out var unboundAvailable);
        Assert.False(unboundAvailable);
        Assert.True(unbound.Missing);
        var noList = new FakeWaypointIpc { ListThrows = new NotSupportedException() };
        var noListReader = noList.Reader();
        noListReader.Read(Target, out _, out var noListAvailable);
        noListReader.Read(Target, out _, out _);
        Assert.False(noListAvailable);
        Assert.True(noListReader.Missing);
        Assert.Equal(1, noList.CountCalls);
        Assert.Equal(1, noList.ListCalls);
    }

    [Fact]
    public void Waypoints_APassingFailure_IsRetried_AndUnavailableMeanwhile()
    {
        // Any other exception isn't "missing": the count is read again next tick. Until the list is read, the final
        // target stands in for the next node, so the waypoints are unavailable.
        var ipc = new FakeWaypointIpc { CountThrows = new InvalidOperationException() };
        var reader = ipc.Reader();
        Assert.Equal(Target, reader.Read(Target, out _, out var available));
        Assert.False(available);
        Assert.False(reader.Missing);
        ipc.CountThrows = null;
        ipc.ListThrows = new InvalidOperationException();
        Assert.Equal(Target, reader.Read(Target, out _, out var listFailed));
        Assert.False(listFailed);
        Assert.Equal(Target, reader.Read(Target, out _, out _));
        // The list isn't read again until the count changes.
        Assert.Equal(1, ipc.ListCalls);
        ipc.ListThrows = null;
        ipc.Count = 5;
        Assert.Equal(Waypoint, reader.Read(Target, out _, out var recovered));
        Assert.True(recovered);
        Assert.Equal(4, ipc.CountCalls);
    }

    [Fact]
    public void Stuck_WithoutTheWaypoints_TheNoProgressCheckIsOff_TheCantPathCheckIsnt()
    {
        // 07-A15: with an older vnavmesh the node is the final target, and an ordinary detour away from it would look
        // stuck after 10 s. So the no-progress check is off for that Call; "no path at all" still stops it.
        var clock = new FakeClock();
        var stuck = new CallStuckDetector(clock.Get);
        stuck.Observe(Target, Target, 0, false, 50, false, true, false, false);
        for (clock.Now = 250; clock.Now <= 120_000; clock.Now += 250)
            Assert.Equal(CallStuckReason.None, stuck.Observe(Target, Target, 0, false, 50 + clock.Now / 10_000f, false, true, false, false));

        clock.Now += CallStuckDetector.CannotPathMs;
        Assert.Equal(CallStuckReason.None, stuck.Observe(Target, Target, 0, false, 70, false, false, false, false));
        clock.Now += CallStuckDetector.CannotPathMs;
        Assert.Equal(CallStuckReason.CannotPath, stuck.Observe(Target, Target, 0, false, 70, false, false, false, false));

        // A passing failure holds the window for that tick only.
        var passing = new CallStuckDetector(clock.Get);
        clock.Now = 0;
        passing.Observe(Target, Waypoint, 6, true, 10, false, true, false, false);
        clock.Now = 5_000;
        Assert.Equal(CallStuckReason.None, passing.Observe(Target, Waypoint, 6, false, 10, false, true, false, false));
        clock.Now = 14_999;
        Assert.Equal(CallStuckReason.None, passing.Observe(Target, Waypoint, 6, true, 10, false, true, false, false));
        clock.Now = 15_000;
        Assert.Equal(CallStuckReason.NoProgress, passing.Observe(Target, Waypoint, 6, true, 10, false, true, false, false));
    }

    [Fact]
    public void Stuck_TakingOffOrLanding_SetsTheBestDistanceAfresh_WithoutRestartingTheWindow()
    {
        // 07-A15: on the ground the distance is XZ (10). A steep take-off adds the height at once (3D 14); climbing out
        // and closing in to 11.5 is 2.5 better than the take-off, so it is headway, though never 2 better than 10.
        var clock = new FakeClock();
        var stuck = new CallStuckDetector(clock.Get);
        stuck.Observe(Target, Waypoint, 6, true, 10, false, true, false, false);
        clock.Now = 2_000;
        Assert.Equal(CallStuckReason.None, stuck.Observe(Target, Waypoint, 6, true, 14, true, true, false, false));
        for (clock.Now = 2_500; clock.Now < 9_000; clock.Now += 500)
            Assert.Equal(CallStuckReason.None, stuck.Observe(Target, Waypoint, 6, true, 14, true, true, false, false));
        clock.Now = 9_000;
        Assert.Equal(CallStuckReason.None, stuck.Observe(Target, Waypoint, 6, true, 11.5f, true, true, false, false));
        clock.Now = 18_999;
        Assert.Equal(CallStuckReason.None, stuck.Observe(Target, Waypoint, 6, true, 11.5f, true, true, false, false));
        clock.Now = 19_000;
        Assert.Equal(CallStuckReason.NoProgress, stuck.Observe(Target, Waypoint, 6, true, 11.5f, true, true, false, false));

        // The flip itself isn't headway, and doesn't restart the window: landing (3D 12, XZ 8) at 8 s with no headway
        // after it is stuck at 10 s.
        var landing = new CallStuckDetector(clock.Get);
        clock.Now = 0;
        landing.Observe(Target, Waypoint, 6, true, 12, true, true, false, false);
        clock.Now = 8_000;
        Assert.Equal(CallStuckReason.None, landing.Observe(Target, Waypoint, 6, true, 8, false, true, false, false));
        clock.Now = 9_999;
        Assert.Equal(CallStuckReason.None, landing.Observe(Target, Waypoint, 6, true, 7, false, true, false, false));
        clock.Now = 10_000;
        Assert.Equal(CallStuckReason.NoProgress, landing.Observe(Target, Waypoint, 6, true, 7, false, true, false, false));

        // A flip during a pause (mounting) is seen on the next unpaused tick.
        var mounted = new CallStuckDetector(clock.Get);
        clock.Now = 0;
        mounted.Observe(Target, Waypoint, 6, true, 10, false, true, false, false);
        clock.Now = 1_000;
        mounted.Observe(Target, Waypoint, 6, true, 14, true, false, false, true);
        clock.Now = 2_000;
        mounted.Observe(Target, Waypoint, 6, true, 14, true, true, false, false);
        clock.Now = 3_000;
        Assert.Equal(CallStuckReason.None, mounted.Observe(Target, Waypoint, 6, true, 11.5f, true, true, false, false));
        clock.Now = 12_999;
        Assert.Equal(CallStuckReason.None, mounted.Observe(Target, Waypoint, 6, true, 11.5f, true, true, false, false));
    }

    [Fact]
    public void Service_TheVnavWaypoints_AreReadThroughTheGuardedReader()
    {
        // 07-A11, 07-A15: bound once, called only through the reader, "not ready" means missing; a waiting phase or a
        // cancel reads the list afresh, and a new Call tries a missing IPC again. Said once a Call, at Debug.
        var source = CallTravelSource;
        Assert.Contains("_pi.GetIpcSubscriber<int>(\"vnavmesh.Path.NumWaypoints\")", source);
        Assert.Contains("_pi.GetIpcSubscriber<List<Vector3>>(\"vnavmesh.Path.ListWaypoints\")", source);
        Assert.Single(Regex.Matches(source, Regex.Escape("_vnavNumWaypoints?.InvokeFunc()")));
        Assert.Single(Regex.Matches(source, Regex.Escape("_vnavListWaypoints?.InvokeFunc()")));
        Assert.Contains(
            "_waypoints = new CallWaypointReader(\n            () => _vnavNumWaypoints?.InvokeFunc(),\n            () => _vnavListWaypoints?.InvokeFunc(),\n            ex => ex is IpcNotReadyError);",
            source);
        Assert.Single(Regex.Matches(source, Regex.Escape("_waypoints.Read(")));
        Assert.DoesNotContain("ReadVnavProgressNode", source);
        var tick = ProfileSettingsTests.MethodBody(source, "    private bool TickStuckDetection()");
        Assert.Contains("if (_waypoints.Missing && !waypointIpcMissing)\n            DebugCall(", tick);
        var reset = ProfileSettingsTests.MethodBody(source, "    private void ResetStuckDetection(bool newCall)");
        Assert.Contains("_waypoints.Reset(newCall);", reset);
        Assert.Contains("ResetStuckDetection(newCall: true);", ProfileSettingsTests.MethodBody(source, "    private async Task RequestAsync(CallPayload payload, bool localDebug)"));
        Assert.Contains("ResetStuckDetection(newCall: false);", ProfileSettingsTests.MethodBody(source, "    private void CancelInternal(bool landingGrace = true)"));
        Assert.Single(Regex.Matches(source, Regex.Escape("ResetStuckDetection(newCall: true)")));
    }

    [Fact]
    public void Service_TheDoorApproach_SurvivesTheEntryStepsThrottledTicks()
    {
        // 07-A15: the stuck detector counts a door approach only while the flag is set. It used to be cleared at the top
        // of every Teleporting tick and set again only past the entry step's 250 ms throttle, so the detector was paused
        // 14 ticks in 15. Now the throttled return leaves it alone; only branches that aren't approaching clear it.
        var source = CallTravelSource;
        var entry = ProfileSettingsTests.MethodBody(source, "    private unsafe void TryProgressIndoorHouseEntry()");
        var throttle = entry.IndexOf("if (now - _lastHouseEnterAttemptUtc < HouseEnterAttemptCooldown)", StringComparison.Ordinal);
        Assert.True(throttle >= 0);
        var afterThrottle = entry[throttle..];
        Assert.StartsWith(
            "if (now - _lastHouseEnterAttemptUtc < HouseEnterAttemptCooldown)\n            return;\n        _lastHouseEnterAttemptUtc = now;",
            afterThrottle);
        // Nothing between the last flag write before the throttle and the throttle clears it on the way to that return.
        var beforeThrottle = entry[..throttle];
        var lastClear = beforeThrottle.LastIndexOf("_doorApproachActive = false;", StringComparison.Ordinal);
        Assert.True(lastClear >= 0);
        var closing = beforeThrottle.IndexOf("return;", lastClear, StringComparison.Ordinal);
        Assert.True(closing > lastClear && closing < throttle, "each clear before the throttle is inside a branch that returns");
        Assert.Equal(2, Regex.Matches(afterThrottle, Regex.Escape("_doorApproachActive = _houseEntrancePathStarted;")).Count);

        // Teleporting doesn't clear it on every tick: each clear sits in a branch (in transit, off the ward, a hop, the
        // apartment Yes/No, inside, the Lifestream handoff), never at the top level of the method.
        var teleport = ProfileSettingsTests.MethodBody(source, "    private void TickTeleporting()");
        foreach (Match clear in Regex.Matches(teleport, Regex.Escape("_doorApproachActive = false;")))
        {
            var line = teleport.LastIndexOf('\n', clear.Index) + 1;
            Assert.True(clear.Index - line > 8, "a clear at the top level of TickTeleporting");
        }

        Assert.Equal(6, Regex.Matches(teleport, Regex.Escape("_doorApproachActive = false;")).Count);
        Assert.Contains(
            "if (IsBetweenAreas() || IsLifestreamBusy() || IsPlayerCasting())\n        {\n            _doorApproachActive = false;",
            teleport);
        Assert.Contains("if (!IsAtHousingWard())\n            {\n                _doorApproachActive = false;", teleport);
        // At the door (interacting), confirming, waiting for the Yes/No or dismounting isn't approaching.
        Assert.Contains("if (entrance is not null && dist <= HouseEntranceInteractYalms)\n        {\n            _doorApproachActive = false;", entry);
        Assert.Equal(5, Regex.Matches(entry, Regex.Escape("_doorApproachActive = false;")).Count);
        // A phase change ends it (a failed prompt, then a retry, starts without it).
        var setPhase = ProfileSettingsTests.MethodBody(source, "    private void SetPhase(CallPhase next)");
        Assert.Contains("if (previous != next)\n            _doorApproachActive = false;", setPhase);
    }

    // ---------- 07-A5: destination validation ----------

    private sealed class FakeSheets : ICallSheetFacts
    {
        public Dictionary<uint, CallWorldFacts> Worlds { get; } = new()
        {
            [40] = new CallWorldFacts(true, 1),
            [41] = new CallWorldFacts(true, 1),
            [50] = new CallWorldFacts(true, 2),
            [60] = new CallWorldFacts(false, 1),
        };

        public Dictionary<uint, CallTerritoryFacts> Territories { get; } = new()
        {
            [132] = new CallTerritoryFacts(0, 0, 200, 0, 0), // a city (Gridania): ±512 + margin
            [137] = new CallTerritoryFacts(0, 0, 100, 0, 0), // a field: ±1024 + margin
            [1036] = new CallTerritoryFacts(0, 0, 100, 0, 0), // a field without an aetheryte
            [1044] = new CallTerritoryFacts(55, 0, 100, 0, 0), // a duty
            [1050] = new CallTerritoryFacts(0, 77, 100, 0, 0), // a quest battle
            [339] = new CallTerritoryFacts(0, 0, 400, 0, 0), // Mist
            [340] = new CallTerritoryFacts(0, 0, 400, 0, 0), // The Lavender Beds
        };

        public HashSet<uint> Aetherytes { get; } = [132, 137, 339, 340];

        public bool TryGetWorld(uint worldId, out CallWorldFacts world) => Worlds.TryGetValue(worldId, out world);

        public bool TryGetTerritory(uint territoryId, out CallTerritoryFacts territory) => Territories.TryGetValue(territoryId, out territory);

        public bool HasAetheryte(uint territoryId) => Aetherytes.Contains(territoryId);
    }

    private static readonly CallDollContext Doll = new(40, 0);

    private static CallPayload Field() => new()
    {
        RequestId = "r",
        From = "OWNERKY1",
        To = "DOLLKEY1",
        WorldId = 41,
        TerritoryId = 137,
        X = 300,
        Y = 40,
        Z = -250,
    };

    private static CallPayload House(bool indoor = false, int plot = 12, int division = 1) => new()
    {
        RequestId = "r",
        From = "OWNERKY1",
        To = "DOLLKEY1",
        WorldId = 40,
        TerritoryId = 339,
        X = 50,
        Y = 20,
        Z = -30,
        HousingCity = 8,
        HousingWard = 14,
        HousingDivision = division,
        HousingPlot = plot,
        HousingIndoor = indoor,
    };

    private static CallPayload Apartment() => new()
    {
        RequestId = "r",
        From = "OWNERKY1",
        To = "DOLLKEY1",
        WorldId = 40,
        TerritoryId = 340,
        X = 2,
        Y = 1,
        Z = 3,
        HousingCity = 2,
        HousingWard = 30,
        HousingDivision = 2,
        HousingApartment = 61,
        HousingIsApartment = true,
        HousingIndoor = true,
    };

    private static CallValidation Validate(CallPayload call, FakeSheets? sheets = null, CallDollContext? doll = null) =>
        CallRequestRules.Validate(call, doll ?? Doll, sheets ?? new FakeSheets());

    [Fact]
    public void Validate_ValidFieldAndHousingCalls_Pass()
    {
        Assert.True(Validate(Field()).Ok);
        Assert.True(Validate(House()).Ok);
        Assert.True(Validate(House(indoor: true)).Ok);
        Assert.True(Validate(House(plot: 0)).Ok); // an outdoor street Call
        Assert.True(Validate(House(plot: 45, division: 2)).Ok);
        Assert.True(Validate(House(plot: 12, division: 2)).Ok);
        Assert.True(Validate(Apartment()).Ok);
        // The subdivision lies far outside the district map's own ±256 (Lifestream's plot data: up to ~900 out).
        var sub = House(plot: 40, division: 2);
        sub.X = -880;
        sub.Z = -870;
        Assert.True(Validate(sub).Ok);
        // Interior coordinates aren't bounded.
        var inside = House(indoor: true);
        inside.X = 5_000;
        Assert.True(Validate(inside).Ok);
    }

    public static TheoryData<string, Action<CallPayload>> BadField => new()
    {
        { "unknown world", c => c.WorldId = 99 },
        { "unknown world", c => c.WorldId = 0 },
        { "world isn't public", c => c.WorldId = 60 },
        { "another data center", c => c.WorldId = 50 },
        { "unknown territory", c => c.TerritoryId = 9999 },
        { "unknown territory", c => c.TerritoryId = 0 },
        { "duty territory", c => c.TerritoryId = 1044 },
        { "quest battle territory", c => c.TerritoryId = 1050 },
        { "no aetheryte in that territory", c => c.TerritoryId = 1036 },
        { "position isn't finite", c => c.X = float.NaN },
        { "position isn't finite", c => c.Y = float.PositiveInfinity },
        { "position isn't finite", c => c.Z = float.NegativeInfinity },
        { "position out of bounds", c => c.X = 1024 + CallRequestRules.PositionMarginYalms + 1 },
        { "position out of bounds", c => c.Z = -(1024 + CallRequestRules.PositionMarginYalms + 1) },
        { "position out of bounds", c => c.Y = CallRequestRules.MaxAbsHeight + 1 },
        { "position out of bounds", c => { c.TerritoryId = 132; c.X = 512 + CallRequestRules.PositionMarginYalms + 1; } },
        { "unknown housing city", c => c.HousingWard = 3 },
        { "unknown housing city", c => c.HousingIndoor = true },
    };

    [Theory]
    [MemberData(nameof(BadField))]
    public void Validate_EachBadFieldCase_IsRefused(string reason, Action<CallPayload> spoil)
    {
        var call = Field();
        spoil(call);
        Assert.Equal(CallValidation.Reject(reason), Validate(call));
    }

    public static TheoryData<string, Action<CallPayload>> BadHousing => new()
    {
        { "unknown housing city", c => c.HousingCity = 7 },
        { "unknown housing city", c => c.HousingCity = 0 },
        { "territory isn't that city's district", c => c.TerritoryId = 340 },
        { "ward out of range", c => c.HousingWard = 0 },
        { "ward out of range", c => c.HousingWard = CallRequestRules.MaxHousingWard + 1 },
        { "ward out of range", c => c.HousingWard = -1 },
        { "division out of range", c => c.HousingDivision = 0 },
        { "division out of range", c => c.HousingDivision = 3 },
        { "plot out of range", c => c.HousingPlot = 61 },
        { "plot out of range", c => c.HousingPlot = -1 },
        { "apartment out of range", c => { c.HousingIsApartment = true; c.HousingPlot = 0; c.HousingApartment = 1000; } },
        { "apartment with a plot", c => c.HousingIsApartment = true },
        { "apartment number on a house", c => c.HousingApartment = 5 },
        { "indoor house with an unknown plot", c => { c.HousingIndoor = true; c.HousingPlot = 0; } },
        { "position out of bounds", c => c.X = CallRequestRules.ResidentialCoordinateBound + CallRequestRules.PositionMarginYalms + 1 },
    };

    [Theory]
    [MemberData(nameof(BadHousing))]
    public void Validate_EachBadHousingCase_IsRefused(string reason, Action<CallPayload> spoil)
    {
        var call = House();
        spoil(call);
        Assert.Equal(CallValidation.Reject(reason), Validate(call));
    }

    [Fact]
    public void Validate_OwnWorldUnknown_IsRefused()
    {
        Assert.Equal(CallValidation.Reject("own world unknown"), Validate(Field(), doll: new CallDollContext(0, 137)));
        Assert.Equal(CallValidation.Reject("own world unknown"), Validate(Field(), doll: new CallDollContext(12345, 137)));
    }

    [Fact]
    public void Validate_A10_ASameZoneCall_NeedsNoAetheryteThere()
    {
        // Old Gridania, the Firmament...: aethernet only, no IsAetheryte row. She is already there and paths locally.
        var aethernetOnly = Field();
        aethernetOnly.TerritoryId = 1036;
        Assert.True(Validate(aethernetOnly, doll: new CallDollContext(40, 1036)).Ok);
        // From anywhere else she would have to teleport there: refused.
        Assert.Equal(CallValidation.Reject("no aetheryte in that territory"), Validate(aethernetOnly, doll: new CallDollContext(40, 137)));
        Assert.Equal(CallValidation.Reject("no aetheryte in that territory"), Validate(aethernetOnly, doll: new CallDollContext(40, 0)));
        // The other checks still apply in her own territory.
        var duty = Field();
        duty.TerritoryId = 1044;
        Assert.Equal(CallValidation.Reject("duty territory"), Validate(duty, doll: new CallDollContext(40, 1044)));
        aethernetOnly.X = float.NaN;
        Assert.Equal(CallValidation.Reject("position isn't finite"), Validate(aethernetOnly, doll: new CallDollContext(40, 1036)));

        // The service passes her territory.
        var request = ProfileSettingsTests.MethodBody(CallTravelSource, "    private async Task RequestAsync(CallPayload payload, bool localDebug)");
        Assert.Contains("new CallDollContext(_objects.LocalPlayer?.CurrentWorld.RowId ?? 0, _clientState.TerritoryType)", request);
    }

    [Fact]
    public void Validate_A10_APlotAbove30_IsReadAsTheSubdivision_AsTravelReadsIt()
    {
        // The owner's indoor manager-API fallback sends Lifestream's 31-60 plot with division 1.
        Assert.True(Validate(House(indoor: true, plot: 45, division: 1)).Ok);
        Assert.True(Validate(House(plot: 31, division: 1)).Ok);
        Assert.True(Validate(House(plot: 60, division: 0)).Ok);
        Assert.Equal(2, CallRequestRules.NormalisedDivision(House(plot: 45, division: 1)));
        Assert.Equal(2, CallRequestRules.NormalisedDivision(House(plot: 45, division: 0)));
        Assert.Equal(1, CallRequestRules.NormalisedDivision(House(plot: 30, division: 1)));
        Assert.Equal(0, CallRequestRules.NormalisedDivision(House(plot: 12, division: 0)));
        // An apartment's division is never inferred from a plot (it has none; one with a plot is refused anyway).
        var apartment = Apartment();
        apartment.HousingDivision = 1;
        apartment.HousingPlot = 45;
        Assert.Equal(1, CallRequestRules.NormalisedDivision(apartment));
        Assert.Equal(CallValidation.Reject("apartment with a plot"), Validate(apartment));
        // Anything else is still checked as sent.
        Assert.Equal(CallValidation.Reject("division out of range"), Validate(House(plot: 12, division: 0)));
        Assert.Equal(CallValidation.Reject("plot out of range"), Validate(House(plot: 61, division: 1)));
    }

    [Fact]
    public void Validate_D9_AnIndoorHouseNeedsAKnownPlot()
    {
        Assert.False(CallRequestRules.IsIndoorPlotKnown(indoor: true, isApartment: false, plot: 0));
        Assert.False(CallRequestRules.IsIndoorPlotKnown(indoor: true, isApartment: false, plot: 61));
        Assert.True(CallRequestRules.IsIndoorPlotKnown(indoor: true, isApartment: false, plot: 1));
        Assert.True(CallRequestRules.IsIndoorPlotKnown(indoor: true, isApartment: false, plot: 60));
        Assert.True(CallRequestRules.IsIndoorPlotKnown(indoor: true, isApartment: true, plot: 0));
        Assert.True(CallRequestRules.IsIndoorPlotKnown(indoor: false, isApartment: false, plot: 0));

        // The owner's origin gate refuses one too, with a chat line, before sending.
        var send = ProfileSettingsTests.MethodBody(RelayClientSource, "    public async Task SendCallAsync(string dollKey)");
        var gate = send.IndexOf("if (!CallRequestRules.IsIndoorPlotKnown(housing.Indoor, housing.IsApartment, housing.Plot))", StringComparison.Ordinal);
        Assert.True(gate >= 0);
        Assert.True(gate < send.IndexOf("_pendingCalls.Add(requestId);", StringComparison.Ordinal));
        var block = send[gate..send.IndexOf("return;", gate, StringComparison.Ordinal)];
        Assert.Contains("PluginChat.PrintError(_chat, CallIndoorPlotUnknownFailure);", block);
    }

    [Fact]
    public void Validate_TheCityTable_MatchesHousingCallLocation()
    {
        var housing = ProfileSettingsTests.PluginSource(Path.Combine("Services", "HousingCallLocation.cs"));
        var cities = Regex.Matches(housing, @"public const int (City\w+) = (\d+);")
            .ToDictionary(m => m.Groups[1].Value, m => int.Parse(m.Groups[2].Value));
        var map = ProfileSettingsTests.MethodBody(housing, "    public static uint? TryGetResidentialTerritoryForCity(int city)");
        var pairs = Regex.Matches(map, @"(City\w+) => (\d+),")
            .ToDictionary(m => cities[m.Groups[1].Value], m => uint.Parse(m.Groups[2].Value));
        Assert.Equal(5, pairs.Count);
        Assert.Equal(pairs.OrderBy(p => p.Key), CallRequestRules.ResidentialTerritoryByCity.OrderBy(p => p.Key));
    }

    [Fact]
    public void Service_NoPayloadWorldName_ReachesTravel()
    {
        var source = CallTravelSource;
        Assert.DoesNotMatch(@"\.WorldName\b", source);
        Assert.DoesNotMatch(@"\bWorldName\s*=", source);
        Assert.Contains("var worldName = ResolveWorldName(call.WorldId);", ProfileSettingsTests.MethodBody(source, "    private bool TryChangeWorld(PendingCall call)"));
        Assert.Contains("var worldName = ResolveWorldName(call.WorldId);", ProfileSettingsTests.MethodBody(source, "    private bool TryGoToHousing(PendingCall call)"));

        // Every Call (the debug recall too) is validated before anything is set up.
        var request = ProfileSettingsTests.MethodBody(source, "    private async Task RequestAsync(CallPayload payload, bool localDebug)");
        var validate = request.IndexOf("CallRequestRules.Validate(", StringComparison.Ordinal);
        Assert.True(validate >= 0 && validate < request.IndexOf("_pending = new PendingCall", StringComparison.Ordinal));
        Assert.Contains("RequestAsync(payload, localDebug: true)", ProfileSettingsTests.MethodBody(source, "    private bool TryRecallDebugPoint()"));

        // New owners still send it: old dolls prefer it.
        Assert.Contains("WorldName = worldName,", ProfileSettingsTests.MethodBody(RelayClientSource, "    public async Task SendCallAsync(string dollKey)"));
    }

    // ---------- 07-A6: housing Yes/No ----------

    private static HousingYesnoContext Entrance(long interactedMs = 1_000, long nowMs = 3_000) =>
        new(ApartmentCall: false, NowMs: nowMs, EntranceInteractedMs: interactedMs, InRightWard: false, LifestreamBusy: false, SelectRoomSeenMs: null);

    private static HousingYesnoContext ApartmentContext(bool ward = true, bool busy = true, long? roomSeen = 2_500) =>
        new(ApartmentCall: true, NowMs: 3_000, EntranceInteractedMs: null, InRightWard: ward, LifestreamBusy: busy, SelectRoomSeenMs: roomSeen);

    [Theory]
    [InlineData(YesnoLanguage.English, "Enter the estate hall?")]
    [InlineData(YesnoLanguage.Japanese, "「ハウス」へ入りますか？")]
    [InlineData(YesnoLanguage.ChineseSimplified, "要进入这间房屋吗？")]
    [InlineData(YesnoLanguage.ChineseTraditional, "要進入這間房屋嗎？")]
    [InlineData(YesnoLanguage.German, "Das Gebäude betreten?")]
    [InlineData(YesnoLanguage.French, "Entrer dans la maison ?")]
    [InlineData(YesnoLanguage.Korean, "'주택'으로 들어가시겠습니까?")]
    public void Yesno_EachLanguagesExactEntrancePrompt_WithinFiveSeconds_IsConfirmed(YesnoLanguage language, string prompt)
    {
        Assert.True(HousingYesnoMatcher.ShouldConfirm(prompt, language, Entrance()));
        Assert.True(HousingYesnoMatcher.ShouldConfirm(prompt, language, Entrance(interactedMs: 0, nowMs: HousingYesnoMatcher.EntranceWindowMs)));
        Assert.False(HousingYesnoMatcher.ShouldConfirm(prompt, language, Entrance(interactedMs: 0, nowMs: HousingYesnoMatcher.EntranceWindowMs + 1)));
    }

    [Theory]
    [InlineData("Enter the estate\u00A0hall?")]
    [InlineData("Enter\u202Fthe estate hall?")]
    [InlineData("  Enter the  estate hall? ")]
    [InlineData("Enterthe estatehall?")]
    public void Yesno_Whitespace_IsNormalised(string prompt)
    {
        Assert.True(HousingYesnoMatcher.ShouldConfirm(prompt, YesnoLanguage.English, Entrance()));
        Assert.True(HousingYesnoMatcher.ShouldConfirm("Entrer dans la maison\u202F?", YesnoLanguage.French, Entrance()));
    }

    [Theory]
    [InlineData("Join Anna Lyse's party?")]
    [InlineData("Leave the estate hall?")]
    [InlineData("Purchase this plot of land for 3,000,000 gil?")]
    [InlineData("Relinquish this estate?")]
    [InlineData("Enter the estate hall? Purchase it?")]
    [InlineData("estate")]
    [InlineData("maison")]
    [InlineData("Gebäude")]
    [InlineData("enter the estate hall?")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Yesno_AnythingElse_IsRefused(string? prompt)
    {
        foreach (var language in Enum.GetValues<YesnoLanguage>())
            Assert.False(HousingYesnoMatcher.ShouldConfirm(prompt, language, Entrance()), $"{language}: '{prompt}'");
    }

    [Fact]
    public void Yesno_TheWrongLanguagesTable_IsRefused()
    {
        Assert.False(HousingYesnoMatcher.ShouldConfirm("Das Gebäude betreten?", YesnoLanguage.English, Entrance()));
        Assert.False(HousingYesnoMatcher.ShouldConfirm("Enter the estate hall?", YesnoLanguage.German, Entrance()));
        Assert.False(HousingYesnoMatcher.ShouldConfirm("Enter the estate hall?", YesnoLanguage.Unknown, Entrance()));
    }

    [Fact]
    public void Yesno_WithoutOurOwnInteraction_IsRefused()
    {
        var none = Entrance() with { EntranceInteractedMs = null };
        Assert.False(HousingYesnoMatcher.ShouldConfirm("Enter the estate hall?", YesnoLanguage.English, none));
        // An interaction "in the future" (a clock mix-up) doesn't count either.
        Assert.False(HousingYesnoMatcher.ShouldConfirm("Enter the estate hall?", YesnoLanguage.English, Entrance(interactedMs: 4_000, nowMs: 3_000)));
        // An apartment Call never takes the house prompt.
        Assert.False(HousingYesnoMatcher.ShouldConfirm("Enter the estate hall?", YesnoLanguage.English, ApartmentContext()));
    }

    [Theory]
    [InlineData(YesnoLanguage.English, "Enter Anna Lyse's room?")]
    [InlineData(YesnoLanguage.English, "Enter Anna Lyse’s room?")]
    [InlineData(YesnoLanguage.English, "Enter\u00A0Anna Lyse's room?")]
    [InlineData(YesnoLanguage.German, "Wohnung Nr. 61 betreten?")]
    [InlineData(YesnoLanguage.German, "Wohnung Nr 61 von Anna Lyse betreten?")]
    public void Yesno_TheApartmentConfirm_WithItsContext_IsConfirmed(YesnoLanguage language, string prompt)
    {
        Assert.Equal(YesnoDecision.Confirm, HousingYesnoMatcher.Decide(prompt, language, ApartmentContext()));
        Assert.Equal(YesnoDecision.Ignore, HousingYesnoMatcher.Decide(prompt, language, ApartmentContext(ward: false)));
        Assert.Equal(YesnoDecision.Ignore, HousingYesnoMatcher.Decide(prompt, language, ApartmentContext(busy: false)));
        Assert.Equal(YesnoDecision.Ignore, HousingYesnoMatcher.Decide(prompt, language, ApartmentContext(roomSeen: null)));
        Assert.Equal(YesnoDecision.Ignore, HousingYesnoMatcher.Decide(prompt, language, ApartmentContext(roomSeen: 3_000 - HousingYesnoMatcher.SelectRoomWindowMs - 1)));
        // Not an apartment Call: no.
        Assert.Equal(YesnoDecision.Ignore, HousingYesnoMatcher.Decide(prompt, language, Entrance()));
    }

    [Theory]
    [InlineData(YesnoLanguage.English, "Join Anna Lyse's party?")]
    [InlineData(YesnoLanguage.English, "Enter the estate hall?")]
    [InlineData(YesnoLanguage.English, "Leave Anna Lyse's room?")]
    [InlineData(YesnoLanguage.English, "Enter Anna Lyse's room? Purchase?")]
    [InlineData(YesnoLanguage.German, "Das Gebäude betreten?")]
    [InlineData(YesnoLanguage.German, "Enter Anna Lyse's room?")]
    [InlineData(YesnoLanguage.English, "")]
    public void Yesno_AnApartmentCall_RefusesAnyOtherPrompt(YesnoLanguage language, string prompt)
    {
        Assert.Equal(YesnoDecision.Ignore, HousingYesnoMatcher.Decide(prompt, language, ApartmentContext()));
    }

    [Theory]
    [InlineData(YesnoLanguage.Japanese)]
    [InlineData(YesnoLanguage.French)]
    public void Yesno_D10_AnApartmentInJapaneseOrFrench_OnlyHints(YesnoLanguage language)
    {
        Assert.False(HousingYesnoMatcher.HasApartmentPattern(language));
        Assert.Equal(YesnoDecision.Hint, HousingYesnoMatcher.Decide("何かの部屋に入りますか？", language, ApartmentContext()));
        Assert.False(HousingYesnoMatcher.ShouldConfirm("Entrer dans l'appartement ?", language, ApartmentContext()));
        // No hint without the context either.
        Assert.Equal(YesnoDecision.Ignore, HousingYesnoMatcher.Decide("何か", language, ApartmentContext(busy: false)));
    }

    [Fact]
    public void Yesno_ClientLanguageNames_Map()
    {
        Assert.Equal(YesnoLanguage.English, HousingYesnoMatcher.FromClientLanguage("English"));
        Assert.Equal(YesnoLanguage.Japanese, HousingYesnoMatcher.FromClientLanguage("Japanese"));
        Assert.Equal(YesnoLanguage.German, HousingYesnoMatcher.FromClientLanguage("German"));
        Assert.Equal(YesnoLanguage.French, HousingYesnoMatcher.FromClientLanguage("French"));
        Assert.Equal(YesnoLanguage.Unknown, HousingYesnoMatcher.FromClientLanguage("Klingon"));
        Assert.Equal(YesnoLanguage.Unknown, HousingYesnoMatcher.FromClientLanguage(null));
    }

    [Fact]
    public void Yesno_A10_RawTextIsLogged_OnlyForAnApartmentConfirmInALanguageWithNoKnownText()
    {
        const string japanese = "何かの部屋に入りますか？";
        // Those texts still need recording (debug-only): an apartment Call in the right ward, the room list just seen.
        Assert.True(HousingYesnoMatcher.MayLogPromptText(YesnoLanguage.Japanese, ApartmentContext()));
        Assert.Equal($"'{japanese}'", HousingYesnoMatcher.PromptLogDetail(japanese, YesnoLanguage.Japanese, ApartmentContext()));
        Assert.Equal("'Entrer ?'", HousingYesnoMatcher.PromptLogDetail("Entrer ?", YesnoLanguage.French, ApartmentContext(busy: false)));
        var longText = new string('x', HousingYesnoMatcher.LoggedPromptMaxChars + 5);
        Assert.Equal($"'{longText[..HousingYesnoMatcher.LoggedPromptMaxChars]}…'", HousingYesnoMatcher.PromptLogDetail(longText, YesnoLanguage.French, ApartmentContext()));

        // Anywhere else only the length: a known language, outside the ward, a stale room list, a house Call.
        Assert.Equal("len=12 matched=no", HousingYesnoMatcher.PromptLogDetail("Enter Bob's?", YesnoLanguage.English, ApartmentContext()));
        Assert.Equal("len=12 matched=no", HousingYesnoMatcher.PromptLogDetail("Enter Bob's?", YesnoLanguage.German, ApartmentContext()));
        Assert.Equal($"len={japanese.Length} matched=no", HousingYesnoMatcher.PromptLogDetail(japanese, YesnoLanguage.Japanese, ApartmentContext(ward: false)));
        Assert.Equal($"len={japanese.Length} matched=no", HousingYesnoMatcher.PromptLogDetail(japanese, YesnoLanguage.Japanese, ApartmentContext(roomSeen: null)));
        Assert.Equal(
            $"len={japanese.Length} matched=no",
            HousingYesnoMatcher.PromptLogDetail(japanese, YesnoLanguage.Japanese, ApartmentContext(roomSeen: 3_000 - HousingYesnoMatcher.SelectRoomWindowMs - 1)));
        Assert.Equal("len=11 matched=no", HousingYesnoMatcher.PromptLogDetail("Join party?", YesnoLanguage.Japanese, Entrance()));
        Assert.Equal("len=11 matched=no", HousingYesnoMatcher.PromptLogDetail("Join party?", YesnoLanguage.Unknown, Entrance()));
        Assert.False(HousingYesnoMatcher.MayLogPromptText(YesnoLanguage.French, Entrance()));
        Assert.Equal("len=0 matched=no", HousingYesnoMatcher.PromptLogDetail(null, YesnoLanguage.Japanese, ApartmentContext()));
    }

    [Fact]
    public void Service_TheYesnoLog_CarriesNoPromptText_ExceptThroughTheRule()
    {
        var source = CallTravelSource;
        var confirm = ProfileSettingsTests.MethodBody(source, "    private unsafe bool TryConfirmHousingEntranceYesno(bool apartment, out string detail)");
        // Every detail that mentions the prompt goes through PromptLogDetail; a confirmed one has no text at all.
        var code = string.Join("\n", confirm.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        var promptUses = Regex.Matches(code, @"(?<before>[\w.]*\(?)\bprompt\b(?!\w)").Select(m => m.Groups["before"].Value).ToList();
        Assert.Equal(["", "Decide(", "PromptLogDetail(", "PromptLogDetail("], promptUses.Select(b => b.Replace("HousingYesnoMatcher.", string.Empty)));
        Assert.Contains("detail = $\"addon#{index} → Yes\";", confirm);
        Assert.Equal(2, Regex.Matches(confirm, Regex.Escape("HousingYesnoMatcher.PromptLogDetail(prompt, language, context.Value)")).Count);
        Assert.DoesNotContain("TruncateForDebug", source);
        // Logged again only when it changes, so it can't alternate with another line every tick.
        Assert.Contains(
            "if (detail != \"no addon\" && !string.Equals(detail, _lastYesnoIgnoredDetail, StringComparison.Ordinal))\n            {\n                _lastYesnoIgnoredDetail = detail;\n                DebugCall($\"yesno: not confirmed ({detail})\", key: \"yesno-ignored\", throttle: false);",
            confirm);
        Assert.Contains("_lastYesnoIgnoredDetail = null;", ProfileSettingsTests.MethodBody(source, "    private async Task RequestAsync(CallPayload payload, bool localDebug)"));
    }

    [Fact]
    public void Service_TheYesno_IsDecidedOnlyByTheMatcher()
    {
        var source = CallTravelSource;
        var confirm = ProfileSettingsTests.MethodBody(source, "    private unsafe bool TryConfirmHousingEntranceYesno(bool apartment, out string detail)");
        Assert.Single(Regex.Matches(source, Regex.Escape("FireCallbackInt(")));
        var decide = confirm.IndexOf("switch (HousingYesnoMatcher.Decide(prompt, language, context.Value))", StringComparison.Ordinal);
        var yes = confirm.IndexOf("case YesnoDecision.Confirm:\n                        addon->FireCallbackInt(0); // Yes", StringComparison.Ordinal);
        Assert.True(decide >= 0 && yes > decide);
        Assert.DoesNotContain(".Contains(", confirm);
        Assert.DoesNotContain("matched", confirm);
        Assert.DoesNotContain("_awaitingHouseEnterConfirm", source);
        Assert.DoesNotContain("HouseEnterConfirmText", source);
        Assert.DoesNotMatch("\"(estate|maison|Gebäude|ハウス|房屋|주택)\"", source);
        // Our own interaction starts the 5-s window, and the room list is tracked each tick.
        Assert.Contains("_houseEnterInteractedMs = _clock();", source);
        Assert.Contains("TrackSelectRoom();", ProfileSettingsTests.MethodBody(source, "    public void Tick()"));
        Assert.Contains("GetAddonByName(\"MansionSelectRoom\")", ProfileSettingsTests.MethodBody(source, "    private void TrackSelectRoom()"));
    }

    // ---------- 07-A7: the prompt read ----------

    [Fact]
    public void Service_ThePromptRead_NeverReadsAnAtkValueString()
    {
        var source = CallTravelSource;
        var read = ProfileSettingsTests.MethodBody(source, "    private static unsafe string ReadSelectYesnoPrompt(AtkUnitBase* addon)");
        Assert.Contains("((AddonSelectYesno*)addon)->PromptText", read);
        Assert.True(read.IndexOf("->PromptText", StringComparison.Ordinal) < read.IndexOf("NodeList[15]", StringComparison.Ordinal));
        Assert.Contains("addon->UldManager.NodeList is not null", read);
        // No AtkValue is read anywhere in the service (a string read without its type check crashed the game).
        Assert.DoesNotContain("AtkValues[", source);
        Assert.DoesNotMatch(@"\.String\.(HasValue|ToString)", source);

        var catalog = SelfTestCatalog.Find("P-ui-selectyesno");
        Assert.NotNull(catalog);
        Assert.Contains(catalog!.ManualSteps, step => step.Contains("PromptText", StringComparison.Ordinal));
        Assert.DoesNotContain(catalog.ManualSteps, step => step.Contains("NodeList[15] is the text node", StringComparison.Ordinal));
    }

    // ---------- 07-A8: location-free logs ----------

    [Fact]
    public void Logs_NoForcedLine_CarriesALocationOrAnExceptionMessage()
    {
        var source = CallTravelSource;
        var forced = ForcedCalls(source, "DebugCall(");
        Assert.True(forced.Count >= 5, $"only {forced.Count} forced lines found");
        foreach (var call in forced)
        {
            Assert.StartsWith("DebugCall(CallLogRules.Forced(", call);
            AssertLocationFree(call);
        }

        // What reaches them: every failure detail and edge-case reason.
        foreach (var call in Calls(source, "EnterFailedPrompt(").Concat(Calls(source, "ReportEdgeCaseError(")))
        {
            var firstArgument = FirstArgument(call);
            AssertLocationFree(firstArgument);
        }

        // The owner side's forced lines: no territory.
        foreach (var call in ForcedCalls(RelayClientSource, "CallTravelDebugLog.Write("))
        {
            AssertLocationFree(call);
            Assert.DoesNotContain("TerritoryType", call);
        }

        var origin = ProfileSettingsTests.MethodBody(RelayClientSource, "    private unsafe bool IsCallOriginReachable(out string failure)");
        Assert.DoesNotContain("RowId}", origin);
    }

    [Fact]
    public void Logs_AForcedLine_HasOnlyTheReasonPhaseKindAndExceptionType()
    {
        Assert.Equal("FAILED: Travel timed out. | phase=Teleporting kind=housing indoor",
            CallLogRules.Forced("FAILED: Travel timed out.", CallPhase.Teleporting, CallRequestRules.CoarseKind(true, true, false)));
        Assert.Equal("FAILED: Unhandled tick exception. | phase=Pathing kind=field ex=NullReferenceException",
            CallLogRules.Forced("FAILED: Unhandled tick exception.", CallPhase.Pathing, CallRequestRules.CoarseKind(false, false, false), nameof(NullReferenceException)));
        Assert.Equal("apartment", CallRequestRules.CoarseKind(true, true, true));
        Assert.Equal("housing outdoor", CallRequestRules.CoarseKind(true, false, false));
    }

    [Fact]
    public void Logs_TheFileRotatesAt1MiB_KeepingOne()
    {
        Assert.Equal(1024 * 1024, CallLogRules.RotateAtBytes);
        Assert.False(CallLogRules.ShouldRotate(CallLogRules.RotateAtBytes - 100, 100));
        Assert.True(CallLogRules.ShouldRotate(CallLogRules.RotateAtBytes - 100, 101));
        Assert.True(CallLogRules.ShouldRotate(5 * CallLogRules.RotateAtBytes, 1));
        // An empty (or missing) file is never rotated, even for a huge line.
        Assert.False(CallLogRules.ShouldRotate(0, 2 * CallLogRules.RotateAtBytes));

        var log = ProfileSettingsTests.PluginSource(Path.Combine("Services", "CallTravelDebugLog.cs"));
        // 11-C3: it ships in Release (its forced lines are location-free).
        Assert.False(log.StartsWith("#if", StringComparison.Ordinal), "CallTravelDebugLog.cs is still gated");
        var rotate = log.IndexOf("CallLogRules.ShouldRotate(existing.Length, Encoding.UTF8.GetByteCount(line))", StringComparison.Ordinal);
        var move = log.IndexOf("File.Move(path, path + CallLogRules.RotatedSuffix, overwrite: true);", StringComparison.Ordinal);
        var append = log.IndexOf("File.AppendAllText(path, line);", StringComparison.Ordinal);
        Assert.True(rotate >= 0 && rotate < move && move < append);
        Assert.Equal(".1", CallLogRules.RotatedSuffix);
    }

    // ---------- 07-A9: fold-ins ----------

    [Fact]
    public void DebugStoreAndRecall_AreQueuedToTheTick()
    {
        var window = ProfileSettingsTests.PluginSource(Path.Combine("Ui", "ConfigWindow.cs"));
        Assert.Contains("if (ImGui.Button(\"Store\"))\n            _relay.RequestStoreDebugCallPoint();", window);
        Assert.Contains("if (ImGui.Button(\"Recall\"))\n            _relay.RequestRecallDebugCallPoint();", window);
        Assert.DoesNotContain("TryStoreDebugCallPoint", window);
        Assert.DoesNotContain("TryRecallDebugCallPoint", window);

        var source = CallTravelSource;
        Assert.Contains("public void RequestStoreDebugPoint() => _storeDebugRequested = true;", source);
        Assert.Contains("public void RequestRecallDebugPoint() => _recallDebugRequested = true;", source);
        Assert.Contains("private bool TryStoreDebugPoint()", source);
        Assert.Contains("private bool TryRecallDebugPoint()", source);
        var tick = ProfileSettingsTests.MethodBody(source, "    public void Tick()");
        Assert.True(tick.IndexOf("TickDebugRequests();", StringComparison.Ordinal) < tick.IndexOf("if (_pending is null)", StringComparison.Ordinal));
    }

    [Fact]
    public void DebugRecall_IsRefusedDuringTheSafewordPause_BeforeAnythingStarts()
    {
        // 07-A10: before RequestAsync starts anything (07-B2: it no longer aborts her own travel on arrival).
        var recall = ProfileSettingsTests.MethodBody(CallTravelSource, "    private bool TryRecallDebugPoint()");
        var paused = recall.IndexOf("if (_timer.IsRemoteControlPaused)", StringComparison.Ordinal);
        Assert.True(paused >= 0);
        var block = recall[paused..recall.IndexOf("return false;", paused, StringComparison.Ordinal)];
        Assert.Contains("PluginChat.PrintError(_chat, \"Call recall isn't available while remote control is paused.\");", block);
        Assert.True(paused < recall.IndexOf("RequestAsync(payload, localDebug: true)", StringComparison.Ordinal));
    }

    [Fact]
    public void ConsentIsRecheckedEveryTick_AndCancelsSilently()
    {
        var source = CallTravelSource;
        var consent = ProfileSettingsTests.MethodBody(source, "    private bool ConsentLost()");
        Assert.Contains("!_config.IsDoll", consent);
        Assert.Contains("_timer.IsRemoteControlPaused", consent);
        Assert.Contains("!_localDebugCall && !_canReceiveCallFromKey(_pending.OwnerKey)", consent);
        var tick = ProfileSettingsTests.MethodBody(source, "    public void Tick()");
        var check = tick.IndexOf("if (ConsentLost())", StringComparison.Ordinal);
        Assert.True(check >= 0 && check < tick.IndexOf("switch (_phase)", StringComparison.Ordinal));
        var block = tick[check..tick.IndexOf("return;", check, StringComparison.Ordinal)];
        Assert.Contains("FinishAsync(CallResultStatuses.Cancelled,", block);
        Assert.DoesNotContain("PluginChat", block);

        Assert.Contains("_consent.CanReceiveCallFromKey);", ProfileSettingsTests.PluginSource("Plugin.cs"));
    }

    [Fact]
    public void FireAndForgetSends_AreLogged()
    {
        var source = CallTravelSource;
        Assert.DoesNotMatch(@"_\s*=\s*(FinishAsync|AckAsync|RequestAsync)\(", source);
        // The original eleven, with the acks behind one gated helper (Ack, FlushAck) and the new expiry, stuck and consent exits,
        // and 07-B1's Decline.
        Assert.Equal(11, Regex.Matches(source, @"\bForget\((FinishAsync|AckAsync|RequestAsync)\(").Count);
        var forget = ProfileSettingsTests.MethodBody(source, "    private void Forget(Task task, string what)");
        Assert.Contains("_log.Debug(", forget);
        // 08-P1: through the shared helper.
        Assert.Contains("FireAndForget.Run(task,", forget);
        Assert.Contains("TaskContinuationOptions.OnlyOnFaulted", ProfileSettingsTests.PluginSource(Path.Combine("Services", "FireAndForget.cs")));
    }

    // ---------- self-test (04, read-only) ----------

    [Fact]
    public void SelfTest_JudgesTheCallIdleState()
    {
        static CheckResult Judge(CallIdleFact? fact) =>
            SessionFactsJudge.Judge(new SessionSnapshot { CallIdle = fact }).Single(r => r.Id == "X-call-idle");

        Assert.Equal(CheckStatus.Pass, Judge(new CallIdleFact(false, false, false, false)).Status);
        Assert.Equal(CheckStatus.Fail, Judge(new CallIdleFact(false, false, true, false)).Status);
        Assert.Equal(CheckStatus.Fail, Judge(new CallIdleFact(false, false, false, true)).Status);
        Assert.Equal(CheckStatus.Skip, Judge(new CallIdleFact(true, false, true, true)).Status);
        Assert.Equal(CheckStatus.Info, Judge(new CallIdleFact(false, true, true, false)).Status);
        Assert.Equal(CheckStatus.Skip, Judge(null).Status);
        Assert.NotNull(SelfTestCatalog.Find("X-call-idle"));

        var host = ProfileSettingsTests.PluginSource(Path.Combine("SelfTest", "Adapters", "SelfTestHost.cs"));
        Assert.Contains("CallIdle = callIdle(),", host);
        Assert.Contains("present && world.IsPublic && world.DataCenter.RowId != 0", host);
        Assert.Contains("\"its Map has a SizeFactor\", map is { SizeFactor: > 0 }", host);
    }

    // ---------- source helpers ----------

    private static string CallTravelSource => ProfileSettingsTests.PluginSource(Path.Combine("Services", "CallTravelService.cs"));

    private static string RelayClientSource => ProfileSettingsTests.PluginSource(Path.Combine("Services", "RelayClient.cs"));

    private static readonly string[] LocationMarkers =
    [
        "FormatPendingSummary", "FormatPathGateSummary", "FormatLocalHousingSnapshot", "FormatDebugStoredSummary",
        "pos=", "terr=", "world=", "{ex}", "ex.Message", "Position", "TerritoryId", "WorldId", "OwnerKey", "PairingKey",
    ];

    private static void AssertLocationFree(string text)
    {
        foreach (var marker in LocationMarkers)
            Assert.False(text.Contains(marker, StringComparison.Ordinal), $"'{marker}' in: {text}");
    }

    /// <summary>Each call of <paramref name="callee"/> (up to its matching parenthesis) that passes <c>force: true</c>.</summary>
    private static List<string> ForcedCalls(string source, string callee) =>
        Calls(source, callee).Where(c => c.Contains("force: true", StringComparison.Ordinal)).ToList();

    /// <summary>Each call of <paramref name="callee"/> in <paramref name="source"/>, from its name to its matching parenthesis (definitions skipped).</summary>
    private static List<string> Calls(string source, string callee)
    {
        var calls = new List<string>();
        for (var at = source.IndexOf(callee, StringComparison.Ordinal); at >= 0; at = source.IndexOf(callee, at + 1, StringComparison.Ordinal))
        {
            if (at > 0 && (char.IsLetterOrDigit(source[at - 1]) || source[at - 1] == '_'))
                continue;
            var lineStart = source.LastIndexOf('\n', at) + 1;
            if (source[lineStart..at].Contains("void ", StringComparison.Ordinal))
                continue;

            var depth = 0;
            var end = at + callee.Length - 1;
            for (; end < source.Length; end++)
            {
                if (source[end] == '(')
                    depth++;
                else if (source[end] == ')' && --depth == 0)
                    break;
            }

            calls.Add(source[at..(end + 1)]);
        }

        return calls;
    }

    /// <summary>The first argument of a call's text (up to a top-level comma or the closing parenthesis).</summary>
    private static string FirstArgument(string call)
    {
        var open = call.IndexOf('(');
        var depth = 0;
        for (var i = open; i < call.Length; i++)
        {
            if (call[i] is '(' or '{' or '[')
                depth++;
            else if (call[i] is ')' or '}' or ']')
                depth--;
            if ((call[i] == ',' && depth == 1) || depth == 0)
                return call[(open + 1)..i];
        }

        return call[(open + 1)..];
    }
}
