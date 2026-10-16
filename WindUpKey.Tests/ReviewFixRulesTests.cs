using System.Text;
using WindUpKey.Services;
using WindUpKey.Sources;

namespace WindUpKey.Tests;

/// <summary>
/// Issue 06 Part A, the Dalamud-free gates behind the review fixes: the Moodles probe and retry gate (06-A3), the
/// commendation tracker (06-A5) and the sound clip ledger (06-A6).
/// </summary>
public sealed class ReviewFixRulesTests
{
    private static readonly DateTime T0 = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);

    // ---------- 06-A3: Moodles ----------

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void Moodles_WhenTheStatusIsNotWanted_NothingIsProbed(bool enabled, bool isDoll, bool loggedIn)
    {
        var gate = new MoodlesSyncGate();

        for (var i = 0; i < 3; i++)
            Assert.Equal(MoodlesStep.Off, gate.Next(enabled, isDoll, loggedIn, moodlesAvailable: false, T0.AddSeconds(i * 30)));

        // Nothing was counted as a probe meanwhile: turning it on probes at once.
        Assert.Equal(MoodlesStep.Probe, gate.Next(true, true, true, moodlesAvailable: false, T0.AddSeconds(61)));
    }

    [Fact]
    public void Moodles_WhileUnavailable_IsProbedAtMostEveryTenSeconds()
    {
        var gate = new MoodlesSyncGate();

        Assert.Equal(MoodlesStep.Probe, gate.Next(true, true, true, false, T0));
        Assert.Equal(MoodlesStep.Wait, gate.Next(true, true, true, false, T0.AddSeconds(1.0 / 60)));
        Assert.Equal(MoodlesStep.Wait, gate.Next(true, true, true, false, T0.AddSeconds(9.9)));
        Assert.Equal(MoodlesStep.Probe, gate.Next(true, true, true, false, T0.AddSeconds(10)));
        Assert.Equal(MoodlesStep.Wait, gate.Next(true, true, true, false, T0.AddSeconds(15)));
        Assert.False(gate.TryStartProbe(T0.AddSeconds(19)));
        Assert.True(gate.TryStartProbe(T0.AddSeconds(20)));
    }

    [Fact]
    public void Moodles_WhenAvailable_IsReadyWithoutProbing()
    {
        var gate = new MoodlesSyncGate();

        Assert.Equal(MoodlesStep.Ready, gate.Next(true, true, true, moodlesAvailable: true, T0));
        Assert.Equal(MoodlesStep.Probe, gate.Next(true, true, true, moodlesAvailable: false, T0.AddSeconds(1)));
    }

    [Fact]
    public void Moodles_AfterFailures_BacksOffFromFiveSecondsDoublingToFiveMinutes()
    {
        var gate = new MoodlesSyncGate();
        var now = T0;
        Assert.True(gate.MayApply(WindChargeLevel.Wound, now));

        var expected = new[] { 5, 10, 20, 40, 80, 160, 300, 300 };
        foreach (var seconds in expected)
        {
            gate.ApplyFailed(now);
            Assert.Equal(TimeSpan.FromSeconds(seconds), gate.CurrentBackoff);
            Assert.False(gate.MayApply(WindChargeLevel.Wound, now.AddSeconds(seconds - 0.1)));
            now = now.AddSeconds(seconds);
            Assert.True(gate.MayApply(WindChargeLevel.Wound, now));
        }
    }

    [Fact]
    public void Moodles_Ready_ResetsTheBackoffAndTheProbeTimer()
    {
        var gate = new MoodlesSyncGate();
        Assert.Equal(MoodlesStep.Probe, gate.Next(true, true, true, false, T0));
        gate.MayApply(WindChargeLevel.Low, T0);
        gate.ApplyFailed(T0);
        gate.ApplyFailed(T0);

        gate.OnMoodlesReady();

        Assert.Equal(TimeSpan.Zero, gate.CurrentBackoff);
        Assert.True(gate.MayApply(WindChargeLevel.Low, T0.AddSeconds(1)));
        Assert.Equal(MoodlesStep.Probe, gate.Next(true, true, true, false, T0.AddSeconds(1)));
    }

    [Fact]
    public void Moodles_ALoginOrANewLevelOrASuccess_ResetsTheBackoff()
    {
        var gate = new MoodlesSyncGate();
        gate.MayApply(WindChargeLevel.Low, T0);
        gate.ApplyFailed(T0);
        Assert.False(gate.MayApply(WindChargeLevel.Low, T0.AddSeconds(1)));

        Assert.True(gate.MayApply(WindChargeLevel.NearlySpent, T0.AddSeconds(1)));

        gate.ApplyFailed(T0.AddSeconds(1));
        gate.ResetBackoff();
        Assert.True(gate.MayApply(WindChargeLevel.NearlySpent, T0.AddSeconds(2)));

        gate.ApplyFailed(T0.AddSeconds(2));
        gate.ApplyFailed(T0.AddSeconds(2));
        gate.ApplySucceeded();
        Assert.True(gate.MayApply(WindChargeLevel.NearlySpent, T0.AddSeconds(3)));
        gate.ApplyFailed(T0.AddSeconds(3));
        Assert.Equal(MoodlesSyncGate.FirstBackoff, gate.CurrentBackoff);
    }

    [Fact]
    public void Moodles_TheServiceChecksTheSettingsBeforeAnyProbe()
    {
        var service = ProfileSettingsTests.PluginSource(Path.Combine("Services", "MoodlesWindStatusService.cs"));
        // No probe or bind at construction, and the IPC handlers only set flags.
        var ctor = ProfileSettingsTests.MethodBody(service, "    public MoodlesWindStatusService(");
        Assert.DoesNotContain("TryProbeMoodles()", ctor);
        Assert.DoesNotContain("TryBindMoodlesReflection()", ctor);
        Assert.Contains("private void OnMoodlesReady() => _readyPending = true;", service);
        Assert.Contains("private void OnMoodlesUnloading() => _unloadPending = true;", service);

        var sync = ProfileSettingsTests.MethodBody(service, "    private void SyncDesiredState(");
        var gateIndex = sync.IndexOf("_gate.Next(_config.MoodlesStatusEnabled, _config.IsDoll, _clientState.IsLoggedIn,", StringComparison.Ordinal);
        var probeIndex = sync.IndexOf("TryProbeMoodles()", StringComparison.Ordinal);
        Assert.True(gateIndex >= 0 && probeIndex > gateIndex, "SyncDesiredState probes before checking the settings");
        Assert.Contains("if (step == MoodlesStep.Probe)\n            TryProbeMoodles();", sync);
        Assert.Contains("if (!_gate.MayApply(level, now))", sync);
        Assert.Contains("_gate.ApplyFailed(now);", sync);
    }

    /// <summary>What reaches into Moodles (its IPC, its reflection handles, or a method that does).</summary>
    private static readonly string[] MoodlesCalls =
    [
        "TryProbeMoodles(", "TryBindMoodlesReflection(", ".Invoke(", "ClearApplied(", "ApplyLevel(", "TryContainsStatus(",
        "TryCancelAll(", "SyncDesiredState(", "TryReapplyAutomation(", "_version.", "_moodlesPlugin", "_applyAutomation",
    ];

    [Theory]
    [InlineData("    public void OnLoggedIn()")]
    [InlineData("    public void OnAreaTransition()")]
    [InlineData("    private void TryReapplyAutomation()")]
    [InlineData("    private void SyncDesiredState(")]
    [InlineData("    public void Dispose()")]
    public void Moodles_PendingSignalsAreHandledBeforeAnyCallIntoMoodles(string signature)
    {
        // 06-A12: an Unloading seen since the last tick drops every old handle before anything calls through one.
        var service = ProfileSettingsTests.PluginSource(Path.Combine("Services", "MoodlesWindStatusService.cs"));
        var member = ProfileSettingsTests.MethodBody(service, signature);
        var body = member[member.IndexOf('{')..];
        var signals = body.IndexOf("HandleIpcSignals();", StringComparison.Ordinal);
        Assert.True(signals >= 0, $"{signature.Trim()} doesn't handle the pending signals");
        foreach (var call in MoodlesCalls)
        {
            var at = body.IndexOf(call, StringComparison.Ordinal);
            Assert.True(at < 0 || at > signals, $"{signature.Trim()} reaches {call} before HandleIpcSignals()");
        }
    }

    [Fact]
    public void Moodles_TheTickGoesThroughTheSignalCheck_AndAReadyStillForcesAnApply()
    {
        var service = ProfileSettingsTests.PluginSource(Path.Combine("Services", "MoodlesWindStatusService.cs"));
        var tick = ProfileSettingsTests.MethodBody(service, "    public void Tick()");
        Assert.Contains("            SyncDesiredState();\n", tick);

        var handle = ProfileSettingsTests.MethodBody(service, "    private void HandleIpcSignals()");
        var unload = handle.IndexOf("if (_unloadPending)", StringComparison.Ordinal);
        var ready = handle.IndexOf("if (!_readyPending)", StringComparison.Ordinal);
        Assert.True(unload >= 0 && ready > unload, "Unloading isn't applied before Ready");
        Assert.Contains("ForgetReflection();", handle[unload..ready]);
        Assert.Contains("_forceApplyPending = true;", handle[ready..]);

        var sync = ProfileSettingsTests.MethodBody(service, "    private void SyncDesiredState(");
        Assert.Contains("        if (_forceApplyPending)\n        {\n            _forceApplyPending = false;\n            forceApply = true;\n        }", sync);
    }

    [Fact]
    public void Moodles_ForgetReflection_ClearsEveryReflectionHandle()
    {
        // 06-A3 / 06-A12: every MethodInfo, Type, FieldInfo, delegate (or the assembly) cached from Moodles is dropped.
        var service = ProfileSettingsTests.PluginSource(Path.Combine("Services", "MoodlesWindStatusService.cs"));
        var handles = System.Text.RegularExpressions.Regex.Matches(
                service,
                @"^\s*private (?:readonly )?(?:static )?(?:Assembly|Type|MethodInfo|FieldInfo|PropertyInfo|ConstructorInfo|EventInfo|MemberInfo|Delegate|JsonSerializerOptions|Func<[^>]*>|Action(?:<[^>]*>)?)\?? (?<name>_\w+)\s*[;=]",
                System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => m.Groups["name"].Value)
            .ToList();
        Assert.True(handles.Count >= 11, $"only {handles.Count} reflection handles found; the scan is out of date");
        Assert.Contains("_jsonOptions", handles);

        var forget = ProfileSettingsTests.MethodBody(service, "    private void ForgetReflection()");
        foreach (var handle in handles)
            Assert.Contains($"\n        {handle} = null;", forget);
    }

    [Fact]
    public void Moodles_TheJsonOptionsAreCreatedPerBind_NeverStatic()
    {
        // 06-A13: options used with Moodles' status type cache its metadata; a static instance kept the unloaded
        // Moodles assembly alive.
        var service = ProfileSettingsTests.PluginSource(Path.Combine("Services", "MoodlesWindStatusService.cs"));
        Assert.DoesNotMatch(@"\bstatic\s+(?:readonly\s+)?JsonSerializerOptions\b", service);
        Assert.Contains("private JsonSerializerOptions? _jsonOptions;", service);
        var bind = ProfileSettingsTests.MethodBody(service, "    private void TryBindMoodlesReflection()");
        Assert.Contains("_jsonOptions = new JsonSerializerOptions", bind);
        var apply = ProfileSettingsTests.MethodBody(service, "    private void ApplyLevel(");
        Assert.Contains("JsonSerializer.Serialize(payload, _jsonOptions)", apply);
        Assert.Contains("JsonSerializer.Deserialize(jsonBytes, _myStatusType, _jsonOptions)", apply);
        Assert.Contains("|| _jsonOptions is null)", apply);
    }

    [Fact]
    public void Moodles_TheJsonOptionsHaveTheirOwnResolver()
    {
        // 06-A15: equal options otherwise share System.Text.Json's metadata cache, which kept the old Moodles assembly
        // alive after a quick reload.
        var service = ProfileSettingsTests.PluginSource(Path.Combine("Services", "MoodlesWindStatusService.cs"));
        var bind = ProfileSettingsTests.MethodBody(service, "    private void TryBindMoodlesReflection()");
        var options = bind[bind.IndexOf("_jsonOptions = new JsonSerializerOptions", StringComparison.Ordinal)..];
        options = options[..options.IndexOf("};", StringComparison.Ordinal)];
        Assert.Contains("TypeInfoResolver = new DefaultJsonTypeInfoResolver(),", options);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(service, @"new JsonSerializerOptions\b"));
    }

    // ---------- 06-A13: turning the Moodles status off clears it once ----------

    [Fact]
    public void Moodles_TurningTheStatusOff_AsksForOneClear()
    {
        var gate = new MoodlesSyncGate();
        Assert.False(gate.TakeClearRequest(statusEnabled: false, isDoll: true, loggedIn: true));

        gate.RequestClear();
        Assert.True(gate.TakeClearRequest(statusEnabled: false, isDoll: true, loggedIn: true));
        // Once per request: the following ticks (status still off) never probe or clear again.
        for (var i = 0; i < 3; i++)
        {
            Assert.False(gate.TakeClearRequest(statusEnabled: false, isDoll: true, loggedIn: true));
            Assert.Equal(MoodlesStep.Off, gate.Next(false, true, true, moodlesAvailable: false, T0.AddSeconds(i * 30)));
        }

        // Turning it off twice before a tick is still one clear.
        gate.RequestClear();
        gate.RequestClear();
        Assert.True(gate.TakeClearRequest(statusEnabled: false, isDoll: true, loggedIn: true));
        Assert.False(gate.TakeClearRequest(statusEnabled: false, isDoll: true, loggedIn: true));
    }

    [Fact]
    public void Moodles_TheClearRequest_IsDroppedWhenTheStatusIsWantedAgain()
    {
        // Turned back on for a doll before the tick: the normal sync takes over.
        var gate = new MoodlesSyncGate();
        gate.RequestClear();

        Assert.False(gate.TakeClearRequest(statusEnabled: true, isDoll: true, loggedIn: true));
        Assert.False(gate.TakeClearRequest(statusEnabled: false, isDoll: true, loggedIn: true));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void Moodles_AWinder_ClearsALeftoverWindMoodle(bool enabled, bool isDoll)
    {
        // A permanent wind moodle outlives the role: a doll who became a winder still has it, and others see it.
        var gate = new MoodlesSyncGate();
        gate.RequestClear();

        Assert.True(gate.TakeClearRequest(enabled, isDoll, loggedIn: true));
        Assert.False(gate.TakeClearRequest(enabled, isDoll, loggedIn: true));
    }

    [Fact]
    public void Moodles_TheClearRequest_WaitsForTheLogin()
    {
        // Turned off while logged out: kept until the login instead of dropped, so the leftover is still cleared.
        var gate = new MoodlesSyncGate();
        gate.RequestClear();

        Assert.False(gate.TakeClearRequest(statusEnabled: false, isDoll: true, loggedIn: false));
        Assert.False(gate.TakeClearRequest(statusEnabled: false, isDoll: true, loggedIn: false));
        Assert.True(gate.TakeClearRequest(statusEnabled: false, isDoll: true, loggedIn: true));
        Assert.False(gate.TakeClearRequest(statusEnabled: false, isDoll: true, loggedIn: true));
    }

    [Fact]
    public void Moodles_ALoginOrReadyOrLosingTheStatus_AsksForAClear()
    {
        var service = ProfileSettingsTests.PluginSource(Path.Combine("Services", "MoodlesWindStatusService.cs"));

        var login = ProfileSettingsTests.MethodBody(service, "    public void OnLoggedIn()");
        var request = login.IndexOf("_gate.RequestClear();", StringComparison.Ordinal);
        var sync = login.IndexOf("SyncDesiredState(", StringComparison.Ordinal);
        Assert.True(request >= 0 && sync > request, "a login doesn't ask for a clear before syncing");

        var signals = ProfileSettingsTests.MethodBody(service, "    private void HandleIpcSignals()");
        var ready = signals[signals.IndexOf("_readyPending = false;", StringComparison.Ordinal)..];
        Assert.Contains("_gate.RequestClear();", ready);

        var syncBody = ProfileSettingsTests.MethodBody(service, "    private void SyncDesiredState(");
        Assert.Contains("var wanted = _config.MoodlesStatusEnabled && _config.IsDoll;\n        if (_wasWanted && !wanted)\n            _gate.RequestClear();", syncBody);
        Assert.True(
            syncBody.IndexOf("_wasWanted = wanted;", StringComparison.Ordinal)
            < syncBody.IndexOf("_gate.TakeClearRequest(", StringComparison.Ordinal),
            "losing the status isn't noticed before the clear request is taken");
    }

    [Fact]
    public void Moodles_TheStatus_IsOptIn()
    {
        // Others see the wind moodle through their sync plugin, so a new config doesn't show it.
        var config = ProfileSettingsTests.PluginSource("Configuration.cs");
        Assert.Contains("    public bool MoodlesStatusEnabled { get; set; }\n", config);
    }

    [Fact]
    public void Moodles_TheServiceClearsOnTurnOff_OnTheFrameworkThread()
    {
        var service = ProfileSettingsTests.PluginSource(Path.Combine("Services", "MoodlesWindStatusService.cs"));
        // The UI only asks (a flag); the tick's sync does the work, after the pending signals and before the gate.
        Assert.Contains("public void OnStatusTurnedOff() => _gate.RequestClear();", service);
        var sync = ProfileSettingsTests.MethodBody(service, "    private void SyncDesiredState(");
        var signals = sync.IndexOf("HandleIpcSignals();", StringComparison.Ordinal);
        var take = sync.IndexOf("if (_gate.TakeClearRequest(_config.MoodlesStatusEnabled, _config.IsDoll, _clientState.IsLoggedIn))\n            ClearAfterTurnedOff();", StringComparison.Ordinal);
        var next = sync.IndexOf("_gate.Next(", StringComparison.Ordinal);
        Assert.True(signals >= 0 && take > signals && next > take, "the clear request isn't taken between the signals and the gate");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(service, @"ClearAfterTurnedOff\(\);"));

        // One probe if Moodles isn't known to be available, then a forced clear (a status we don't remember applying).
        var clear = ProfileSettingsTests.MethodBody(service, "    private void ClearAfterTurnedOff()");
        Assert.Contains("        if (!_moodlesAvailable)\n            TryProbeMoodles();\n        if (_moodlesAvailable)\n            ClearApplied(force: true);", clear);

        // The doll's checkbox asks for it when it is turned off.
        var window = ProfileSettingsTests.PluginSource(Path.Combine("Ui", "ConfigWindow.cs"));
        var toggle = window[window.IndexOf("ImGui.Checkbox(\"Show wind charge as Moodle", StringComparison.Ordinal)..];
        toggle = toggle[..toggle.IndexOf("ImGui.Spacing();", StringComparison.Ordinal)];
        Assert.Contains("_config.MoodlesStatusEnabled = moodlesOn;", toggle);
        Assert.Contains("                if (!moodlesOn)\n                    _moodlesStatus.OnStatusTurnedOff();", toggle);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(window, @"OnStatusTurnedOff\(\)"));
    }

    [Fact]
    public void Moodles_TheClearWaitsForTheLocalPlayer_AskingAgain()
    {
        // 06-A15: with no local player (zoning, loading), the request is asked for again instead of being spent without
        // clearing, and nothing is probed until there is one.
        var service = ProfileSettingsTests.PluginSource(Path.Combine("Services", "MoodlesWindStatusService.cs"));
        var clear = ProfileSettingsTests.MethodBody(service, "    private void ClearAfterTurnedOff()");
        var wait = clear.IndexOf("        if (_objects.LocalPlayer is not IPlayerCharacter)\n        {\n            _gate.RequestClear();\n            return;\n        }", StringComparison.Ordinal);
        var probe = clear.IndexOf("TryProbeMoodles();", StringComparison.Ordinal);
        Assert.True(wait >= 0 && probe > wait, "the clear doesn't wait for the local player before probing");

        // Asked again, it is taken on the next tick (still off for a logged-in doll), and only then.
        var gate = new MoodlesSyncGate();
        gate.RequestClear();
        Assert.True(gate.TakeClearRequest(statusEnabled: false, isDoll: true, loggedIn: true));
        gate.RequestClear();
        Assert.True(gate.TakeClearRequest(statusEnabled: false, isDoll: true, loggedIn: true));
        Assert.False(gate.TakeClearRequest(statusEnabled: false, isDoll: true, loggedIn: true));
    }

    // ---------- 06-A5: commendations ----------

    private sealed class FakeClock
    {
        public TimeSpan Now { get; set; } = TimeSpan.FromHours(1);

        public void Advance(TimeSpan by) => Now += by;
    }

    private static (CommendationTracker Tracker, FakeClock Clock) NewTracker()
    {
        var clock = new FakeClock();
        return (new CommendationTracker(() => clock.Now), clock);
    }

    [Fact]
    public void Commendation_AfterACompletedDuty_AwardsOnceFromItsObservedLength()
    {
        var (tracker, clock) = NewTracker();
        tracker.DutyStarted(commendations: 10, isDoll: true);
        Assert.Null(tracker.Update(true, true, true, 10));
        clock.Advance(TimeSpan.FromMinutes(31));
        tracker.DutyCompleted(isDoll: true);
        Assert.Null(tracker.Update(true, false, true, 10));
        clock.Advance(TimeSpan.FromMinutes(1));

        var award = tracker.Update(true, false, true, 12);

        Assert.Equal(new CommendationAward(TimeSpan.FromMinutes(60), 2, TimeSpan.FromMinutes(31), Eligible: true), award);
        Assert.False(tracker.Tracking);
    }

    [Fact]
    public void Commendation_ACompletedDuty_StaysArmedForAtMostFifteenMinutes()
    {
        var (tracker, clock) = NewTracker();
        tracker.DutyStarted(10, true);
        clock.Advance(TimeSpan.FromMinutes(20));
        tracker.DutyCompleted(true);
        clock.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1));
        Assert.Null(tracker.Update(true, false, true, 10));
        Assert.True(tracker.Armed);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(tracker.Update(true, false, true, 10));

        Assert.False(tracker.Tracking);
        Assert.Null(tracker.Update(true, false, true, 11));
    }

    [Fact]
    public void Commendation_JustInsideTheTimeout_StillAwards()
    {
        var (tracker, clock) = NewTracker();
        tracker.DutyStarted(10, true);
        clock.Advance(TimeSpan.FromMinutes(20));
        tracker.DutyCompleted(true);
        clock.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1));

        Assert.NotNull(tracker.Update(true, false, true, 11));
    }

    [Fact]
    public void Commendation_AJoinInProgressWhileArmed_IsMeasuredOnItsOwn()
    {
        // A 60-minute duty ends with no commendation; a few minutes later the doll joins another duty in progress
        // (no DutyStarted event), plays 20 minutes of it, and is commended there.
        var (tracker, clock) = NewTracker();
        tracker.DutyStarted(10, true);
        clock.Advance(TimeSpan.FromMinutes(60));
        tracker.DutyCompleted(true);
        Assert.Null(tracker.Update(true, false, true, 10));
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Null(tracker.Update(true, false, true, 10));

        Assert.Null(tracker.Update(true, true, true, 10));
        Assert.True(tracker.Tracking);
        Assert.False(tracker.Armed);
        clock.Advance(TimeSpan.FromMinutes(20));
        Assert.Null(tracker.Update(true, true, true, 10));
        tracker.DutyCompleted(true);
        clock.Advance(TimeSpan.FromMinutes(1));

        var award = tracker.Update(true, false, true, 11);

        Assert.Equal(TimeSpan.FromMinutes(20), award?.ObservedDuration);
        Assert.Equal(TimeSpan.FromMinutes(40), award?.Bonus);
    }

    [Fact]
    public void Commendation_AStartEventWhileArmed_RestartsObservation()
    {
        var (tracker, clock) = NewTracker();
        tracker.DutyStarted(10, true);
        clock.Advance(TimeSpan.FromMinutes(60));
        tracker.DutyCompleted(true);
        clock.Advance(TimeSpan.FromMinutes(2));

        tracker.DutyStarted(10, true);
        clock.Advance(TimeSpan.FromMinutes(10));
        tracker.DutyCompleted(true);

        Assert.Equal(TimeSpan.FromMinutes(10), tracker.Update(true, false, true, 11)?.ObservedDuration);
    }

    [Fact]
    public void Commendation_IsAwardedAtMostOncePerDuty()
    {
        // Commended while the duty still reads as started (its frames ran all along, so this is no new start).
        var (tracker, clock) = NewTracker();
        tracker.DutyStarted(10, true);
        Assert.Null(tracker.Update(true, true, true, 10));
        clock.Advance(TimeSpan.FromMinutes(30));
        tracker.DutyCompleted(true);
        tracker.DutyCompleted(true);

        Assert.NotNull(tracker.Update(true, true, true, 11));
        // The rest of that duty is ignored: it isn't observed again, so a repeated completion earns nothing.
        Assert.Null(tracker.Update(true, true, true, 11));
        Assert.False(tracker.Tracking);
        tracker.DutyCompleted(true);
        Assert.Null(tracker.Update(true, true, true, 12));
        Assert.Null(tracker.Update(true, true, true, 13));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(tracker.Update(true, false, true, 14));
        Assert.Null(tracker.Update(true, false, true, 15));
    }

    [Fact]
    public void Commendation_LeavingDollAtAnyPoint_MakesTheAwardIneligible()
    {
        var (tracker, clock) = NewTracker();
        tracker.DutyStarted(10, true);
        tracker.Update(true, true, isDoll: false, 10);
        clock.Advance(TimeSpan.FromMinutes(30));
        tracker.DutyCompleted(true);

        var award = tracker.Update(true, false, true, 11);

        Assert.False(award?.Eligible);
    }

    [Fact]
    public void Commendation_ADutyEndingWithoutCompletion_IsForgotten()
    {
        var (tracker, clock) = NewTracker();
        tracker.DutyStarted(10, true);
        clock.Advance(TimeSpan.FromMinutes(30));

        Assert.Null(tracker.Update(true, false, true, 10));

        Assert.False(tracker.Tracking);
        tracker.DutyCompleted(true);
        Assert.Null(tracker.Update(true, false, true, 11));
    }

    // ---------- 06-A6: sound ----------

    /// <summary>06-A12: the ledger runs on a monotonic clock (Stopwatch.Elapsed in the plugin), never wall-clock time.</summary>
    private static readonly TimeSpan M0 = TimeSpan.FromMinutes(7);

    [Fact]
    public void Clips_KeepTheirPinUntilTheirExpectedEndPlusTheMargin()
    {
        var ledger = new ClipLedger<int>();
        Assert.Empty(ledger.Add(1, TimeSpan.FromSeconds(3), M0));

        var end = M0 + TimeSpan.FromSeconds(3) + ClipLedger<int>.Margin;
        Assert.Empty(ledger.Expire(end - TimeSpan.FromMilliseconds(1)));
        Assert.True(ledger.MightBePlaying(end - TimeSpan.FromMilliseconds(1)));
        Assert.False(ledger.MightBePlaying(end));
        Assert.Equal(new[] { 1 }, ledger.Expire(end));
        Assert.Equal(0, ledger.Count);
    }

    [Fact]
    public void Clips_OverlappingWinds_KeepAtMostTwoPins()
    {
        var ledger = new ClipLedger<int>();
        Assert.Empty(ledger.Add(1, TimeSpan.FromSeconds(5), M0));
        Assert.Empty(ledger.Add(2, TimeSpan.FromSeconds(5), M0 + TimeSpan.FromSeconds(1)));

        Assert.Equal(new[] { 1 }, ledger.Add(3, TimeSpan.FromSeconds(5), M0 + TimeSpan.FromSeconds(2)));
        Assert.Equal(2, ledger.Count);
        Assert.Equal(new[] { 2, 3 }, ledger.TakeAll());
        Assert.Equal(0, ledger.Count);
    }

    [Fact]
    public void Clips_ANewPlay_FreesThoseThatHaveEnded()
    {
        var ledger = new ClipLedger<int>();
        ledger.Add(1, TimeSpan.FromSeconds(1), M0);

        Assert.Equal(new[] { 1 }, ledger.Add(2, TimeSpan.FromSeconds(1), M0 + TimeSpan.FromSeconds(10)));
        Assert.Equal(1, ledger.Count);
    }

    [Fact]
    public void Clips_OfUnknownLength_AreKeptThirtySeconds()
    {
        var ledger = new ClipLedger<int>();
        ledger.Add(1, null, M0);

        Assert.True(ledger.MightBePlaying(M0 + ClipLedger<int>.UnknownDuration + ClipLedger<int>.Margin - TimeSpan.FromMilliseconds(1)));
        Assert.False(ledger.MightBePlaying(M0 + ClipLedger<int>.UnknownDuration + ClipLedger<int>.Margin));
    }

    [Fact]
    public void Clips_NothingPlaying_MeansUnloadDoesNotStop()
    {
        var ledger = new ClipLedger<int>();
        Assert.False(ledger.MightBePlaying(M0));
        ledger.Add(1, TimeSpan.FromSeconds(1), M0);
        Assert.False(ledger.MightBePlaying(M0 + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void WavHeader_GivesTheDataLengthOverTheByteRate()
    {
        // 44.1 kHz, 16-bit stereo: 176 400 bytes a second; 2.5 s of silence, with a LIST chunk before the data.
        var wav = Wav(sampleRate: 44100, channels: 2, bits: 16, dataBytes: 441000, extraChunk: true);

        Assert.Equal(TimeSpan.FromSeconds(2.5), WavHeader.Duration(wav));
        Assert.Null(WavHeader.Duration(Encoding.ASCII.GetBytes("not a wav file at all, but long enough to read")));
        Assert.Null(WavHeader.Duration(Wav(44100, 0, 16, 100, extraChunk: false)));
    }

    [Fact]
    public void WavHeader_UsesTheFmtByteRate_SoACompressedWavGetsItsLength()
    {
        // 06-A13: IMA ADPCM (format 0x11), 22.05 kHz mono, 4 bits a sample, 512-byte blocks: 11 100 bytes a second by its
        // fmt chunk. Channels x bits / 8 gives zero bytes a sample here, which left it without a length.
        var adpcm = Wav(sampleRate: 22050, channels: 1, bits: 4, dataBytes: 33300, extraChunk: true, format: 0x11, byteRate: 11100, blockAlign: 512);
        Assert.Equal(TimeSpan.FromSeconds(3), WavHeader.Duration(adpcm));

        // The byte rate is what counts, even where it disagrees with the other fields.
        Assert.Equal(TimeSpan.FromSeconds(2), WavHeader.Duration(Wav(44100, 2, 16, 100000, extraChunk: false, byteRate: 50000)));
        Assert.Null(WavHeader.Duration(Wav(44100, 2, 16, 100, extraChunk: false, byteRate: 0)));
    }

    [Fact]
    public void WavHeader_Pcm_UsesTheLongerOfTheTwoLengths()
    {
        // 06-A15: 44.1 kHz, 16-bit stereo, 441 000 data bytes: 2.5 s at sample rate x block align (176 400 bytes a second).
        // An inflated byte rate (twice that) would give 1.25 s and free the buffer while PlaySound still reads it.
        Assert.Equal(TimeSpan.FromSeconds(2.5), WavHeader.Duration(Wav(44100, 2, 16, 441000, extraChunk: false, byteRate: 352800)));
        // A byte rate below sample rate x block align is the longer one, so it still counts.
        Assert.Equal(TimeSpan.FromSeconds(5), WavHeader.Duration(Wav(44100, 2, 16, 441000, extraChunk: false, byteRate: 88200)));
        // A block align too small (2 for 16-bit stereo) gives the longer length too.
        Assert.Equal(TimeSpan.FromSeconds(5), WavHeader.Duration(Wav(44100, 2, 16, 441000, extraChunk: false, blockAlign: 2)));
    }

    [Fact]
    public void WavHeader_Pcm_WithAZeroSampleRateOrBlockAlign_KeepsTheByteRate()
    {
        // 06-A15: 8 kHz, 8-bit mono, 320 000 data bytes: 40 s at its 8 000-byte rate. A zero block align or sample rate
        // gives no frame rate; the clip keeps its byte-rate length rather than falling back to the shorter 30 s default.
        Assert.Equal(TimeSpan.FromSeconds(40), WavHeader.Duration(Wav(8000, 1, 8, 320000, extraChunk: false, blockAlign: 0)));
        Assert.Equal(TimeSpan.FromSeconds(40), WavHeader.Duration(Wav(0, 1, 8, 320000, extraChunk: false, byteRate: 8000)));
    }

    [Fact]
    public void WavHeader_NonPcm_KeepsTheByteRate()
    {
        // 06-A15: IEEE float (format 3), 44.1 kHz stereo 32-bit, 705 600 data bytes: 2 s at its 352 800-byte rate, even
        // with a block align (4) that gives a smaller rate. The PCM rule is for PCM only.
        var floatWav = Wav(44100, 2, 32, 705600, extraChunk: false, format: 3, blockAlign: 4);
        Assert.Equal(TimeSpan.FromSeconds(2), WavHeader.Duration(floatWav));
    }

    [Fact]
    public void WavHeader_TryRead_FindsTheChunksPastOthers_AndRefusesBrokenOnes()
    {
        var wav = Wav(sampleRate: 44100, channels: 2, bits: 16, dataBytes: 400, extraChunk: true);
        Assert.True(WavHeader.TryRead(wav, out var layout));
        Assert.Equal(new WavLayout(12, 16, 1, 2, 44100, 176400, 4, 16, 12 + 24 + 12 + 8, 400), layout);

        // A chunk running past the end, a short fmt chunk, or a data chunk before any fmt chunk.
        var truncated = wav[..^1];
        Assert.False(WavHeader.TryRead(truncated, out _));
        var shortFmt = (byte[])wav.Clone();
        BitConverter.TryWriteBytes(shortFmt.AsSpan(16, 4), 14);
        Assert.False(WavHeader.TryRead(shortFmt, out _));
        var dataFirst = (byte[])wav.Clone();
        "data"u8.CopyTo(dataFirst.AsSpan(12, 4));
        Assert.False(WavHeader.TryRead(dataFirst, out _));
        Assert.Null(WavHeader.Duration(dataFirst));
    }

    [Fact]
    public void Sound_TruncateWav_SharesTheHeaderParsing()
    {
        // 06-A13: one RIFF chunk parser (WavHeader.TryRead) for the clip length and the truncation.
        var service = ProfileSettingsTests.PluginSource(Path.Combine("Services", "SoundEffectService.cs"));
        var truncate = ProfileSettingsTests.MethodBody(service, "    internal static byte[] TruncateWav(");
        Assert.Contains("if (!WavHeader.TryRead(wav, out var layout))", truncate);
        Assert.Contains("var fmtChunkOffset = layout.FmtChunkOffset;", truncate);
        Assert.DoesNotContain("0x20746D66", service);
        Assert.DoesNotContain("0x61746164", service);
        Assert.DoesNotContain("BitConverter.ToUInt32(", service);
        Assert.DoesNotContain("BitConverter.ToInt32(", service);
    }

    [Fact]
    public void Sound_NoPurgeBeforeAPlay_AndUnloadStopsOnlyWhileOursMightPlay()
    {
        var service = ProfileSettingsTests.PluginSource(Path.Combine("Services", "SoundEffectService.cs"));
        var play = ProfileSettingsTests.MethodBody(service, "    private void PlayCached(");
        Assert.DoesNotContain("StopLocked()", play);
        Assert.DoesNotContain("SndPurge", play);
        Assert.Contains("_pins.Add(pin, WavHeader.Duration(toPlay), _clock.Elapsed)", play);

        var dispose = ProfileSettingsTests.MethodBody(service, "    public void Dispose()");
        Assert.Contains("if (_pins.MightBePlaying(_clock.Elapsed))\n                StopLocked();", dispose);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(service, @"StopLocked\(\);"));
    }

    [Fact]
    public void Sound_ThePinsRunOnAMonotonicClock_NeverWallClockTime()
    {
        // 06-A12: a wall-clock jump forward would free a pinned buffer PlaySound may still be reading.
        var service = ProfileSettingsTests.PluginSource(Path.Combine("Services", "SoundEffectService.cs"));
        Assert.Contains("private readonly Stopwatch _clock = Stopwatch.StartNew();", service);
        Assert.DoesNotContain("DateTime", service);
        Assert.DoesNotContain("Environment.TickCount", service);
        // Every ledger call is given the stopwatch's time (Add, Expire and MightBePlaying once each).
        var calls = System.Text.RegularExpressions.Regex.Matches(service, @"_pins\.(?:Add|Expire|MightBePlaying)\(").Count;
        var onTheClock = System.Text.RegularExpressions.Regex.Matches(service, @"_pins\.(?:Add|Expire|MightBePlaying)\([^;]*?_clock\.Elapsed\)").Count;
        Assert.Equal(3, calls);
        Assert.Equal(calls, onTheClock);

        var ledger = ProfileSettingsTests.PluginSource(Path.Combine("Services", "ClipLedger.cs"));
        Assert.DoesNotContain("DateTime", ledger);
        foreach (var name in new[] { "Add", "Expire", "MightBePlaying" })
        {
            var method = typeof(ClipLedger<int>).GetMethod(name)!;
            Assert.Equal(typeof(TimeSpan), method.GetParameters()[^1].ParameterType);
        }
    }

    private static byte[] Wav(
        int sampleRate, short channels, short bits, int dataBytes, bool extraChunk, short format = 1, int? byteRate = null, short? blockAlign = null)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write("RIFF"u8.ToArray());
        w.Write(0);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write(format);
        w.Write(channels);
        w.Write(sampleRate);
        w.Write(byteRate ?? sampleRate * channels * bits / 8);
        w.Write(blockAlign ?? (short)(channels * bits / 8));
        w.Write(bits);
        if (extraChunk)
        {
            w.Write("LIST"u8.ToArray());
            w.Write(3);
            w.Write(new byte[] { 1, 2, 3, 0 });
        }

        w.Write("data"u8.ToArray());
        w.Write(dataBytes);
        w.Write(new byte[dataBytes]);
        w.Flush();
        return stream.ToArray();
    }
}
