using System.Numerics;
using System.Text.RegularExpressions;
using WindUpKey.Protocol;
using WindUpKey.Services;

namespace WindUpKey.Tests;

/// <summary>
/// Issue 07 Part B: the Call's behaviour. The rules are Dalamud-free (<see cref="CallAskFirst"/>,
/// <see cref="CallQuietPeriod"/>, <see cref="CallResultNotice"/>, <see cref="CallCorroboration"/>);
/// <c>CallTravelService</c>, <c>RelayClient</c>, <c>Plugin</c> and the prompt window need Dalamud, so the checks on their
/// wiring read their source.
/// </summary>
public sealed class CallBehaviourTests
{
    private sealed class FakeClock
    {
        public long Now;

        public long Get() => Now;
    }

    private const string OwnerA = "AAAA1111";
    private const string OwnerB = "BBBB2222";

    // ---------- 07-B1: the callbacks only set flags ----------

    [Fact]
    public void Prompt_TheCallbacksOnlySetFlags_AndTheTickConsumesThem()
    {
        var source = CallTravelSource;
        Assert.Contains("    private void OnPromptAccept() => _acceptRequested = true;\n", source);
        Assert.Contains("    private void OnPromptDecline() => _declineRequested = true;\n", source);
        Assert.Contains("_prompt.OnAccept = OnPromptAccept;", source);
        Assert.Contains("_prompt.OnDecline = OnPromptDecline;", source);
        // stopcall only sets the flag too.
        var request = ProfileSettingsTests.MethodBody(source, "    public bool RequestDecline()");
        Assert.DoesNotContain("FinishAsync", request);
        Assert.Contains("_declineRequested = true;", request);

        // The window's buttons only invoke them.
        var window = ProfileSettingsTests.MethodBody(PromptWindowSource, "    public override void Draw()");
        Assert.Equal(2, Regex.Matches(window, Regex.Escape("OnDecline?.Invoke();")).Count);
        Assert.Single(Regex.Matches(window, Regex.Escape("OnAccept?.Invoke();")));
        Assert.DoesNotContain("IsOpen", window);

        // The tick consumes Decline in any state, before the deadlines and the phase's own work; Accept in the waiting step.
        var tick = ProfileSettingsTests.MethodBody(source, "    public void Tick()");
        var decline = tick.IndexOf("if (_declineRequested)", StringComparison.Ordinal);
        Assert.True(decline >= 0);
        Assert.True(decline < tick.IndexOf("if (TickDeadlines())", StringComparison.Ordinal));
        Assert.True(decline < tick.IndexOf("switch (_phase)", StringComparison.Ordinal));
        Assert.Contains("_declineRequested = false;\n                if (TryDeclineActiveCall())", tick);
        var waiting = ProfileSettingsTests.MethodBody(source, "    private void TickWaiting()");
        Assert.Contains("if (_acceptRequested)\n        {\n            _acceptRequested = false;", waiting);

        // A click from before a phase change, or for an earlier Call, is dropped.
        var setPhase = ProfileSettingsTests.MethodBody(source, "    private void SetPhase(CallPhase next)");
        Assert.Contains("_acceptRequested = false;", setPhase);
        Assert.Contains("_declineRequested = false;", setPhase);
        Assert.DoesNotContain("_callAccepted =", setPhase);
        Assert.Contains("_declineRequested = false;", ProfileSettingsTests.MethodBody(source, "    private void CancelInternal(bool landingGrace = true)"));
    }

    [Fact]
    public void Prompt_EscAndTheCloseButton_CantDismissIt()
    {
        var window = PromptWindowSource;
        Assert.Contains("ShowCloseButton = false;", window);
        Assert.Contains("RespectCloseHotkey = false;", window);
    }

    // ---------- 07-B1 (D4): the quiet period ----------

    [Fact]
    public void Quiet_AutoDeclinesWithin5MinutesOfADecline_PerOwner()
    {
        var clock = new FakeClock { Now = 1_000 };
        var quiet = new CallQuietPeriod(clock.Get);
        Assert.False(quiet.IsQuiet(OwnerA, out _));

        quiet.Record(OwnerA, CallResultStatuses.Declined);
        Assert.True(quiet.IsQuiet(OwnerA, out var minutes));
        Assert.Equal(5, minutes);
        Assert.False(quiet.IsQuiet(OwnerB, out _));

        clock.Now += 4 * 60_000 + 1;
        Assert.True(quiet.IsQuiet(OwnerA, out minutes));
        Assert.Equal(1, minutes);
        Assert.False(quiet.IsQuiet(OwnerB, out _));
    }

    [Fact]
    public void Quiet_ExpiresAtExactly5Minutes()
    {
        var clock = new FakeClock { Now = 10_000 };
        var quiet = new CallQuietPeriod(clock.Get);
        quiet.Record(OwnerA, CallResultStatuses.Declined);

        clock.Now = 10_000 + CallQuietPeriod.DurationMs - 1;
        Assert.True(quiet.IsQuiet(OwnerA, out var minutes));
        Assert.Equal(1, minutes);
        clock.Now = 10_000 + CallQuietPeriod.DurationMs;
        Assert.False(quiet.IsQuiet(OwnerA, out _));
        Assert.Equal(5 * 60_000, CallQuietPeriod.DurationMs);
    }

    [Theory]
    [InlineData(CallResultStatuses.Cancelled)]
    [InlineData(CallResultStatuses.Failed)]
    [InlineData(CallResultStatuses.Arrived)]
    [InlineData("")]
    public void Quiet_OnlyADeclineStartsIt_NeverASafewordCancel(string status)
    {
        var quiet = new CallQuietPeriod(new FakeClock().Get);
        quiet.Record(OwnerA, status);
        Assert.False(quiet.IsQuiet(OwnerA, out _));
    }

    [Fact]
    public void Quiet_ANewDecline_RestartsIt()
    {
        var clock = new FakeClock();
        var quiet = new CallQuietPeriod(clock.Get);
        quiet.Record(OwnerA, CallResultStatuses.Declined);
        clock.Now = 4 * 60_000;
        quiet.Record(OwnerA, CallResultStatuses.Declined);
        clock.Now = 8 * 60_000;
        Assert.True(quiet.IsQuiet(OwnerA, out var minutes));
        Assert.Equal(1, minutes);
    }

    [Fact]
    public void Service_TheQuietPeriod_StartsOnlyFromAFinishedCall_AndAnAutoDeclineDoesntExtendIt()
    {
        var source = CallTravelSource;
        // Every finish reports its status; only Declined starts it (tested above). The safeword and logout cancel.
        var finish = ProfileSettingsTests.MethodBody(source, "    private async Task FinishAsync(string status, string message)");
        Assert.Contains("_quiet.Record(pending.OwnerKey, status);", finish);
        Assert.Single(Regex.Matches(source, Regex.Escape("_quiet.Record(")));
        Assert.Contains(
            "Forget(FinishAsync(CallResultStatuses.Cancelled, \"Call cancelled.\"), \"cancel result\");",
            ProfileSettingsTests.MethodBody(source, "    public void CancelActiveCall()"));
        Assert.Contains("_relay.CancelActiveCall();", ProfileSettingsTests.MethodBody(PluginSource, "    private void OnLogout(int type, int code)"));

        // An arriving Call in the quiet period is answered declined at once, with her chat line, outside Hardcore.
        var request = ProfileSettingsTests.MethodBody(source, "    private async Task RequestAsync(CallPayload payload, bool localDebug)");
        var quiet = request.IndexOf("CallAskFirst.AutoDeclines(_config.HardcoreMode, _quiet.IsQuiet(callerKey, out var quietMinutes))", StringComparison.Ordinal);
        Assert.True(quiet >= 0);
        var block = request[quiet..request.IndexOf("return;", quiet, StringComparison.Ordinal)];
        Assert.Contains("SendResultOrLocalAsync(payload, CallResultStatuses.Declined,", block);
        Assert.Contains("(quiet for {quietMinutes} more min).", block);
        Assert.True(quiet < request.IndexOf("_pending = new PendingCall", StringComparison.Ordinal));

        // Her Decline (and Stop) sends declined, never in Hardcore.
        var decline = ProfileSettingsTests.MethodBody(source, "    private bool TryDeclineActiveCall()");
        Assert.Contains("!CallAskFirst.MayDecline(_config.HardcoreMode)", decline);
        Assert.Contains("Forget(FinishAsync(CallResultStatuses.Declined, \"Call declined.\"), \"decline result\");", decline);
    }

    // ---------- 07-B1 (D3, D5): the ask-first rule ----------

    public static TheoryData<CallGate, bool> EveryGateAndBusy()
    {
        var data = new TheoryData<CallGate, bool>();
        foreach (var gate in Enum.GetValues<CallGate>())
        {
            data.Add(gate, false);
            data.Add(gate, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryGateAndBusy))]
    public void AskFirst_NonHardcore_NeverTravelsWithoutAccept(CallGate gate, bool busy)
    {
        Assert.Equal(CallStart.Wait, CallAskFirst.Decide(hardcore: false, accepted: false, failedPrompt: false, gate, busy));
        Assert.True(CallAskFirst.OffersAccept(hardcore: false, accepted: false, failedPrompt: false));
    }

    [Theory]
    [InlineData(CallGate.Crafting)]
    [InlineData(CallGate.Instance)]
    [InlineData(CallGate.Combat)]
    [InlineData(CallGate.NotReady)]
    public void AskFirst_ARememberedAccept_TravelsWhenTheGateClears_WithNoSecondPrompt(CallGate gate)
    {
        Assert.Equal(CallStart.Wait, CallAskFirst.Decide(hardcore: false, accepted: true, failedPrompt: false, gate, herTravelBusy: false));
        Assert.False(CallAskFirst.OffersAccept(hardcore: false, accepted: true, failedPrompt: false));
        Assert.Equal(CallStart.Travel, CallAskFirst.Decide(hardcore: false, accepted: true, failedPrompt: false, CallGate.None, herTravelBusy: false));
    }

    [Fact]
    public void AskFirst_Hardcore_TravelsWithoutAccept_OnceTheGatesClear()
    {
        Assert.Equal(CallStart.Travel, CallAskFirst.Decide(hardcore: true, accepted: false, failedPrompt: false, CallGate.None, herTravelBusy: false));
        Assert.Equal(CallStart.Wait, CallAskFirst.Decide(hardcore: true, accepted: false, failedPrompt: false, CallGate.Combat, herTravelBusy: false));
        // Information only: no Accept.
        Assert.False(CallAskFirst.OffersAccept(hardcore: true, accepted: false, failedPrompt: false));
    }

    [Fact]
    public void AskFirst_B2_HerOwnTrip_IsStoppedOnlyAfterAcceptOrInHardcore()
    {
        Assert.Equal(CallStart.Wait, CallAskFirst.Decide(hardcore: false, accepted: false, failedPrompt: false, CallGate.None, herTravelBusy: true));
        Assert.Equal(CallStart.StopHerTravelThenTravel, CallAskFirst.Decide(hardcore: false, accepted: true, failedPrompt: false, CallGate.None, herTravelBusy: true));
        Assert.Equal(CallStart.StopHerTravelThenTravel, CallAskFirst.Decide(hardcore: true, accepted: false, failedPrompt: false, CallGate.None, herTravelBusy: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AskFirst_TheFailedPrompt_WaitsForHerRetry_HardcoreToo(bool hardcore)
    {
        Assert.Equal(CallStart.Wait, CallAskFirst.Decide(hardcore, accepted: false, failedPrompt: true, CallGate.None, herTravelBusy: false));
        Assert.True(CallAskFirst.OffersAccept(hardcore, accepted: false, failedPrompt: true));
    }

    [Fact]
    public void AskFirst_Hardcore_RefusesStopcallAndDecline()
    {
        Assert.False(CallAskFirst.MayDecline(hardcore: true));
        Assert.True(CallAskFirst.MayDecline(hardcore: false));
        Assert.Equal("Hardcore: you can't stop a Call.", CallAskFirst.HardcoreStopRefusal);
        // D5: no automatic decline in Hardcore either.
        Assert.False(CallAskFirst.AutoDeclines(hardcore: true, quiet: true));
        Assert.True(CallAskFirst.AutoDeclines(hardcore: false, quiet: true));
        Assert.False(CallAskFirst.AutoDeclines(hardcore: false, quiet: false));

        // stopcall: refused with the chat line before anything is asked of the Call.
        var plugin = PluginSource;
        var stopcall = plugin.IndexOf("if (string.Equals(verb, \"stopcall\", StringComparison.OrdinalIgnoreCase))", StringComparison.Ordinal);
        Assert.True(stopcall >= 0);
        var block = plugin[stopcall..plugin.IndexOf("#endif", stopcall, StringComparison.Ordinal)];
        var refuse = block.IndexOf("if (!CallAskFirst.MayDecline(Configuration.HardcoreMode))\n            {\n                PluginChat.PrintError(ChatGui, CallAskFirst.HardcoreStopRefusal);\n                return;", StringComparison.Ordinal);
        Assert.True(refuse >= 0);
        Assert.True(refuse < block.IndexOf("_relay.RequestDeclineActiveCall()", StringComparison.Ordinal));
        Assert.DoesNotContain("CancelActiveCall", block);

        // No Decline or Stop button in Hardcore, and a stray flag is ignored there.
        var source = CallTravelSource;
        Assert.Equal(2, Regex.Matches(source, Regex.Escape("_prompt.ShowDecline = CallAskFirst.MayDecline(hardcore);")).Count);
        Assert.DoesNotMatch(@"_prompt\.ShowDecline = (?!CallAskFirst\.MayDecline\(hardcore\);)", source);
        Assert.Contains("!CallAskFirst.MayDecline(_config.HardcoreMode)", ProfileSettingsTests.MethodBody(source, "    private bool TryDeclineActiveCall()"));
    }

    [Fact]
    public void Service_TravelStartsOnlyWhereTheAskFirstRuleSaysSo()
    {
        var source = CallTravelSource;
        var waiting = ProfileSettingsTests.MethodBody(source, "    private void TickWaiting()");
        Assert.Contains("switch (CallAskFirst.Decide(hardcore, _callAccepted, failed, gate, herTravel))", waiting);
        // BeginTravel's only callers are the rule's two travel outcomes.
        var callers = Regex.Matches(source, @"(?<!void )\bBeginTravel\(\);").Select(m => m.Index).ToList();
        Assert.Equal(2, callers.Count);
        var at = source.IndexOf("    private void TickWaiting()", StringComparison.Ordinal);
        Assert.All(callers, c => Assert.InRange(c, at, at + waiting.Length));
        Assert.Contains("case CallStart.StopHerTravelThenTravel:", waiting);
        var stop = waiting.IndexOf("case CallStart.StopHerTravelThenTravel:", StringComparison.Ordinal);
        var stopCase = waiting[stop..waiting.IndexOf("case CallStart.Travel:", stop, StringComparison.Ordinal)];
        var abort = stopCase.IndexOf("AbortCallAutomation();", StringComparison.Ordinal);
        Assert.True(abort >= 0 && abort < stopCase.IndexOf("BeginTravel();", StringComparison.Ordinal));

        // Her Accept is remembered, as the rule says (07-B6); a new Call starts unaccepted (her debug recall excepted)
        // and a failure asks again.
        Assert.Contains("_callAccepted = step.Accepted;", waiting);
        Assert.Contains(
            "_callAccepted = localDebug;",
            ProfileSettingsTests.MethodBody(source, "    private async Task RequestAsync(CallPayload payload, bool localDebug)"));
        Assert.Contains("_callAccepted = false;", ProfileSettingsTests.MethodBody(source, "    private void EnterFailedPrompt("));
        Assert.Contains("_callAccepted = false;", ProfileSettingsTests.MethodBody(source, "    private void CancelInternal(bool landingGrace = true)"));
    }

    // ---------- 07-B6: the failed prompt's retry ----------

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AskFirst_B6_AcceptOnTheFailedPrompt_IsTheRetry_BackToWaitingGates(bool hardcore, bool accepted)
    {
        var step = CallAskFirst.OnAccept(hardcore, accepted, CallPhase.WaitingAccept);
        Assert.Equal(new CallAcceptStep(Accepted: true, CallPhase.WaitingGates), step);
        // No longer failed: once the gates clear it travels, Hardcore too.
        Assert.Equal(
            CallStart.Travel,
            CallAskFirst.Decide(hardcore, step.Accepted, failedPrompt: step.Phase == CallPhase.WaitingAccept, CallGate.None, herTravelBusy: false));
    }

    [Fact]
    public void AskFirst_B6_AcceptBeforeTravel_IsRememberedWhereOffered_AndChangesNoPhase()
    {
        // Non-Hardcore: remembered, still waiting for the gates.
        Assert.Equal(new CallAcceptStep(true, CallPhase.WaitingGates), CallAskFirst.OnAccept(hardcore: false, accepted: false, CallPhase.WaitingGates));
        Assert.Equal(new CallAcceptStep(true, CallPhase.WaitingGates), CallAskFirst.OnAccept(hardcore: false, accepted: true, CallPhase.WaitingGates));
        // Hardcore before a failure offers no Accept: a stray click changes nothing.
        Assert.Equal(new CallAcceptStep(false, CallPhase.WaitingGates), CallAskFirst.OnAccept(hardcore: true, accepted: false, CallPhase.WaitingGates));
    }

    [Fact]
    public void Service_B6_TickWaiting_AppliesTheRetry_BeforeItReadsTheFailedPrompt()
    {
        var waiting = ProfileSettingsTests.MethodBody(CallTravelSource, "    private void TickWaiting()");
        var accept = waiting.IndexOf("var step = CallAskFirst.OnAccept(hardcore, _callAccepted, _phase);", StringComparison.Ordinal);
        Assert.True(accept >= 0);
        var retry = waiting.IndexOf(
            "            _callAccepted = step.Accepted;\n"
            + "            if (step.Phase != _phase)\n"
            + "            {\n"
            + "                // A failed retry: the attempt that just failed may still be draining Lifestream/vnav.\n"
            + "                AbortCallAutomation();\n"
            + "                SetPhase(step.Phase);\n"
            + "            }\n",
            StringComparison.Ordinal);
        Assert.True(retry > accept);

        // The failed prompt is read once, after the retry, and before anything uses it.
        var failed = waiting.IndexOf("        var failed = _phase == CallPhase.WaitingAccept;\n", StringComparison.Ordinal);
        Assert.True(failed > retry);
        Assert.Single(Regex.Matches(waiting, @"\bfailed\s*=[^=]"));
        Assert.True(failed < waiting.IndexOf("CallAskFirst.Decide(", StringComparison.Ordinal));
        Assert.True(failed < waiting.IndexOf("var herTravel", StringComparison.Ordinal));
    }

    // ---------- 07-B2: her own travel ----------

    [Theory]
    [MemberData(nameof(EveryGateAndBusy))]
    public void B6_HerTravel_IsReadOnlyWithNoGate_AndNeverOnTheFailedPrompt(CallGate gate, bool failedPrompt)
    {
        Assert.Equal(!failedPrompt && gate == CallGate.None, CallAskFirst.ReadsHerTravel(failedPrompt, gate));
    }

    [Fact]
    public void Service_B6_HerTravel_IsReadOnlyWhereTheRuleSaysSo()
    {
        var source = CallTravelSource;
        var waiting = ProfileSettingsTests.MethodBody(source, "    private void TickWaiting()");
        Assert.Contains("        var herTravel = CallAskFirst.ReadsHerTravel(failed, gate) && IsHerTravelBusy();\n", waiting);
        // That is its only caller: nothing else may abort our own draining on the failed prompt.
        Assert.Single(Regex.Matches(source, @"(?<!bool )\bIsHerTravelBusy\(\)"));
    }

    [Fact]
    public void B2_OwnDrain_IsTheFiveSecondsAfterOurOwnAbort()
    {
        Assert.False(CallAskFirst.IsOwnDrain(null, 1_000));
        Assert.True(CallAskFirst.IsOwnDrain(1_000, 1_000));
        Assert.True(CallAskFirst.IsOwnDrain(1_000, 1_000 + CallAskFirst.OwnDrainMs - 1));
        Assert.False(CallAskFirst.IsOwnDrain(1_000, 1_000 + CallAskFirst.OwnDrainMs));
        Assert.Equal(5_000, CallAskFirst.OwnDrainMs);
    }

    [Fact]
    public void Service_B2_HerOwnTravel_IsntAbortedOnArrival_OrByAnEndThatNeverTravelled()
    {
        var source = CallTravelSource;
        var request = ProfileSettingsTests.MethodBody(source, "    private async Task RequestAsync(CallPayload payload, bool localDebug)");
        Assert.DoesNotContain("AbortCallAutomation", request);
        Assert.DoesNotContain("StopAutomationIpc", request);

        // A finish or a failure stops only what the Call drove.
        Assert.Contains("AbortOurAutomation();", ProfileSettingsTests.MethodBody(source, "    private void CancelInternal(bool landingGrace = true)"));
        Assert.DoesNotContain("AbortCallAutomation", ProfileSettingsTests.MethodBody(source, "    private void CancelInternal(bool landingGrace = true)"));
        Assert.Contains("AbortOurAutomation();", ProfileSettingsTests.MethodBody(source, "    private void EnterFailedPrompt("));
        Assert.DoesNotContain("AbortCallAutomation", ProfileSettingsTests.MethodBody(source, "    private void EnterFailedPrompt("));
        Assert.Contains(
            "        if (_weOwnTravel || CallLockPolicy.IsTravelPhase(_phase))\n            AbortCallAutomation();",
            ProfileSettingsTests.MethodBody(source, "    private void AbortOurAutomation()"));

        // Our own abort is timed; busy travel right after it is ours, aborted again without restarting the window.
        Assert.Contains("_lastOwnAbortMs = _clock();", ProfileSettingsTests.MethodBody(source, "    private void AbortCallAutomation()"));
        var busy = ProfileSettingsTests.MethodBody(source, "    private bool IsHerTravelBusy()");
        Assert.Contains("CallAskFirst.IsOwnDrain(_lastOwnAbortMs, _clock())", busy);
        Assert.Contains("StopAutomationIpc();", busy);
        Assert.DoesNotContain("AbortCallAutomation", busy);
        Assert.DoesNotContain("IsExternallyBusy", source);
    }

    // ---------- 07-B3: the cross-check ----------

    private static CallPayload Field(float x = 10, float y = 0, float z = 10) => new()
    {
        RequestId = "r",
        From = OwnerA,
        To = OwnerB,
        WorldId = 40,
        TerritoryId = 129,
        X = x,
        Y = y,
        Z = z,
    };

    private static CallPayload House(bool indoor) => new()
    {
        RequestId = "r",
        From = OwnerA,
        To = OwnerB,
        WorldId = 40,
        TerritoryId = 339,
        HousingCity = 8,
        HousingWard = 5,
        HousingDivision = 1,
        HousingPlot = 12,
        HousingIndoor = indoor,
    };

    [Fact]
    public void Corroboration_APartyTerritoryMismatch_IsRefused()
    {
        Assert.False(CallCorroboration.Check(Field(), new CallPlace(132, 0), null).Ok);
        Assert.True(CallCorroboration.Check(Field(), new CallPlace(129, 0), null).Ok);
        // A housing Call: her party says another city's district, or a plain zone of the right region.
        Assert.False(CallCorroboration.Check(House(indoor: false), new CallPlace(340, 2), null).Ok);
        Assert.False(CallCorroboration.Check(House(indoor: false), new CallPlace(129, 0), null).Ok);
        Assert.True(CallCorroboration.Check(House(indoor: false), new CallPlace(339, 8), null).Ok);
        // A field Call while the owner sits in a house.
        Assert.False(CallCorroboration.Check(Field(), new CallPlace(282, 8), null).Ok);
    }

    [Fact]
    public void Corroboration_AnIndoorTerritory_MatchesByItsRegionCity()
    {
        // 282 is a Mist cottage interior: not the district's id, but its PlaceNameRegion maps to Limsa (8).
        Assert.True(CallCorroboration.Check(House(indoor: true), new CallPlace(282, 8), null).Ok);
        Assert.False(CallCorroboration.Check(House(indoor: true), new CallPlace(342, 2), null).Ok);
    }

    [Fact]
    public void Corroboration_NothingToCheck_Accepts()
    {
        Assert.True(CallCorroboration.Check(Field(), null, null).Ok);
        // A party member whose territory isn't known yet isn't compared.
        Assert.True(CallCorroboration.Check(Field(), new CallPlace(0, 0), null).Ok);
    }

    [Fact]
    public void Corroboration_B6_AnUnresolvedHousingCity_IsNothingToCheck()
    {
        // 573 stands for an apartment lobby (or a Shirogane or Empyreum interior) whose PlaceNameRegion isn't in the set.
        var unresolved = new CallPlace(573, 0, IsHousing: true);
        Assert.True(CallCorroboration.Check(House(indoor: true), unresolved, null).Ok);
        Assert.True(CallCorroboration.Check(House(indoor: false), unresolved, null).Ok);
        Assert.True(CallCorroboration.Check(House(indoor: true), null, new CallOwnerSighting(Vector3.Zero, unresolved)).Ok);
        Assert.False(CallCorroboration.IsComparable(House(indoor: true), unresolved));

        // Still compared wherever it can be: a resolved city, a zone that isn't housing, and a Call that isn't for
        // housing (its territory id needs no city).
        Assert.False(CallCorroboration.Check(House(indoor: true), new CallPlace(342, 2, IsHousing: true), null).Ok);
        Assert.True(CallCorroboration.Check(House(indoor: true), new CallPlace(282, 8, IsHousing: true), null).Ok);
        Assert.False(CallCorroboration.Check(House(indoor: true), new CallPlace(129, 0), null).Ok);
        Assert.False(CallCorroboration.Check(Field(), unresolved, null).Ok);
        var lobbyField = Field();
        lobbyField.TerritoryId = 573;
        Assert.True(CallCorroboration.Check(lobbyField, unresolved, null).Ok);
    }

    [Fact]
    public void Corroboration_AVisibleOwnerBeyond50Yalms_IsRefused()
    {
        var here = new CallPlace(129, 0);
        Assert.True(CallCorroboration.Check(Field(), null, new CallOwnerSighting(new Vector3(10, 0, 60), here)).Ok);
        Assert.False(CallCorroboration.Check(Field(), null, new CallOwnerSighting(new Vector3(10, 0, 60.1f), here)).Ok);
        Assert.False(CallCorroboration.Check(Field(), null, new CallOwnerSighting(new Vector3(10, 40, 50), here)).Ok);
        Assert.False(CallCorroboration.Check(Field(x: float.NaN), null, new CallOwnerSighting(new Vector3(10, 0, 10), here)).Ok);
        // Seen here, but the Call names another territory.
        Assert.False(CallCorroboration.Check(Field(), null, new CallOwnerSighting(new Vector3(10, 0, 10), new CallPlace(132, 0))).Ok);
        Assert.Equal(50f, CallCorroboration.MaxVisibleDistanceYalms);
    }

    [Fact]
    public void Service_TheCrossCheck_RunsOnceOnArrival_SkipsTheDebugRecall_AndNeverLogsTheKeys()
    {
        var source = CallTravelSource;
        var request = ProfileSettingsTests.MethodBody(source, "    private async Task RequestAsync(CallPayload payload, bool localDebug)");
        var check = request.IndexOf("CallCorroboration.Check(payload, OwnerPartyPlace(callerKey), OwnerSighting(callerKey))", StringComparison.Ordinal);
        var skipDebug = request.IndexOf("if (!localDebug)\n        {\n            var corroboration", StringComparison.Ordinal);
        Assert.True(skipDebug >= 0 && check > skipDebug);
        var validate = request.IndexOf("CallRequestRules.Validate(", StringComparison.Ordinal);
        Assert.True(validate >= 0 && check > validate);
        Assert.True(check < request.IndexOf("_pending = new PendingCall", StringComparison.Ordinal));
        Assert.Single(Regex.Matches(source, Regex.Escape("CallCorroboration.Check(")));
        var block = request[check..request.IndexOf("return;", check, StringComparison.Ordinal)];
        Assert.Contains("CallResultStatuses.Failed", block);
        Assert.DoesNotContain("callerKey", block[block.IndexOf('\n')..]);

        foreach (var member in new[] { "    private CallPlace? OwnerPartyPlace(string ownerKey)", "    private unsafe CallOwnerSighting? OwnerSighting(string ownerKey)" })
        {
            var body = ProfileSettingsTests.MethodBody(source, member);
            Assert.Contains("PairingKeyUtil.FromContentId(", body);
            Assert.DoesNotContain("DebugCall(", body);
            Assert.DoesNotContain("PluginChat", body);
            Assert.Contains("_log.Debug(\"", body);
            foreach (Match log in Regex.Matches(body, @"_log\.\w+\([^;]*;"))
                Assert.EndsWith(", ex.GetType().Name);", log.Value);
        }

        // The party is injected, and a housing interior is matched to its city through PlaceNameRegion.
        Assert.Contains("[PluginService] internal static IPartyList PartyList", PluginSource);
        var place = ProfileSettingsTests.MethodBody(source, "    private CallPlace PlaceOf(uint territoryId)");
        Assert.Contains("HousingCallLocation.TryGetCityForTerritory(territoryId)", place);
        Assert.Contains("row.TerritoryIntendedUse.RowId == HousingIndoorIntendedUse", place);
        Assert.Contains("HousingCallLocation.TryGetCityByPlaceNameRegion(row.PlaceNameRegion.RowId)", place);
        // 07-B6: both kinds are marked housing, a resolved city or not.
        Assert.Contains("return new CallPlace(territoryId, district, IsHousing: true);", place);
        Assert.Contains("return new CallPlace(territoryId, city, IsHousing: true);", place);
        Assert.Equal(2, Regex.Matches(place, Regex.Escape("IsHousing: true")).Count);
    }

    // ---------- 07-B4 (D13): the prompt's content ----------

    [Fact]
    public void Service_ThePromptShowsTheOwnerAndTheDestination_FromLuminaNamesOnly()
    {
        var source = CallTravelSource;
        var describe = ProfileSettingsTests.MethodBody(source, "    private string DescribeDestination(PendingCall call)");
        Assert.Contains("ResolveWorldName(call.WorldId)", describe);
        Assert.Contains("TerritoryName(districtId)", describe);
        Assert.Contains("TerritoryName(call.TerritoryId)", describe);
        Assert.DoesNotContain("payload", describe);
        Assert.DoesNotMatch(@"(?<!Resolve)WorldName", describe);
        Assert.Contains("row.PlaceName.ValueNullable?.Name.ExtractText()", ProfileSettingsTests.MethodBody(source, "    private string TerritoryName(uint territoryId)"));
        Assert.Contains(
            "_pending.Destination = DescribeDestination(_pending);",
            ProfileSettingsTests.MethodBody(source, "    private async Task RequestAsync(CallPayload payload, bool localDebug)"));

        var show = ProfileSettingsTests.MethodBody(source, "    private void ShowWaitingPrompt(");
        Assert.Contains("_prompt.Destination = _pending.Destination;", show);
        Assert.Contains("_prompt.OwnerLabel = OwnerLabel(_pending.OwnerKey);", show);
        // The label is the local pair's, never the payload's.
        Assert.Contains("_config.FindPairByKey(ownerKey)?.GetMessageLabel()", ProfileSettingsTests.MethodBody(source, "    private string OwnerLabel(string ownerKey)"));
    }

    // ---------- 07-B1 (D4): the owner side ----------

    [Fact]
    public void Owner_DeclinedShowsItsMessage_AnyOtherStatusStaysSilent()
    {
        Assert.Equal("Alice declined your Call.", CallResultNotice.ForOwner(CallResultStatuses.Declined, "Alice"));
        Assert.Null(CallResultNotice.ForOwner(CallResultStatuses.Arrived, "Alice"));
        Assert.Null(CallResultNotice.ForOwner(CallResultStatuses.Failed, "Alice"));
        Assert.Null(CallResultNotice.ForOwner(CallResultStatuses.Cancelled, "Alice"));
        Assert.Null(CallResultNotice.ForOwner("teleported", "Alice"));
        Assert.Null(CallResultNotice.ForOwner("DECLINED", "Alice"));
        Assert.Null(CallResultNotice.ForOwner(null, "Alice"));

        // The wire value, short enough for the relay's only check on it (its length).
        Assert.Equal("declined", CallResultStatuses.Declined);
        Assert.True(WireLimits.FitsShort(CallResultStatuses.Declined));

        // Only for a Call of ours still pending; the label is the local one.
        var handler = ProfileSettingsTests.MethodBody(RelayClientSource, "    private void HandleCallResult(Envelope envelope)");
        var take = handler.IndexOf("if (!TryTakePendingCall(payload.RequestId))\n            return;", StringComparison.Ordinal);
        Assert.True(take >= 0);
        var notice = handler.IndexOf("CallResultNotice.ForOwner(payload.Status, GetPairMessageLabel(dollKey))", StringComparison.Ordinal);
        Assert.True(take < notice);
        Assert.Single(Regex.Matches(handler, Regex.Escape("PluginChat.Print(")));
    }

    // ---------- source helpers ----------

    private static string CallTravelSource => ProfileSettingsTests.PluginSource(Path.Combine("Services", "CallTravelService.cs"));

    private static string RelayClientSource => ProfileSettingsTests.PluginSource(Path.Combine("Services", "RelayClient.cs"));

    private static string PromptWindowSource => ProfileSettingsTests.PluginSource(Path.Combine("Ui", "CallPromptWindow.cs"));

    private static string PluginSource => ProfileSettingsTests.PluginSource("Plugin.cs");
}
