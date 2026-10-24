using System.Text.RegularExpressions;
using WindUpKey.SelfTest;
using WindUpKey.Services;
using WindUpKey.Ui;

namespace WindUpKey.Tests;

/// <summary>
/// Issue 06 Part C: cancelling the looping emote with <c>/sit</c> or the game's interrupt-emote command instead of a
/// forward nudge (06-C1), and the self-test's read-only checks of it (06-C2).
/// </summary>
public sealed class EmoteCancelTests
{
    private const int Ticks = 1000;

    /// <summary>One frame at 240 fps, where 30 frames (120 ms) is shorter than one server round trip (06-C3).</summary>
    private const long FrameMs = 4;

    /// <summary>The fake monotonic clock (<c>Environment.TickCount64</c> in the plugin); <see cref="Step"/> advances it.</summary>
    private long _nowMs = 5_000_000;

    /// <summary>One tick <paramref name="ms"/> after the previous one.</summary>
    private EmoteCancelStep Step(EmoteCancelPlan plan, EmoteCancelFacts facts, long ms = FrameMs)
    {
        _nowMs += ms;
        return plan.Next(facts, _nowMs);
    }

    private IEnumerable<EmoteCancelStep> Steps(EmoteCancelPlan plan, EmoteCancelFacts facts, int ticks = Ticks) =>
        Enumerable.Range(0, ticks).Select(_ => Step(plan, facts)).ToList();

    private static EmoteCancelFacts Facts(
        bool ready = true,
        bool locked = false,
        bool sitPose = false,
        bool inLoop = true,
        bool positionLoop = false,
        bool commandAvailable = true) =>
        new(ready, locked, sitPose, inLoop, positionLoop, commandAvailable);

    /// <summary>
    /// Requests once, then ticks <paramref name="ticks"/> times <paramref name="frameMs"/> apart with the same facts;
    /// every step in order.
    /// </summary>
    private List<EmoteCancelStep> Run(EmoteCancelFacts facts, int ticks = Ticks, long frameMs = FrameMs)
    {
        var plan = new EmoteCancelPlan();
        plan.Request();
        var steps = new List<EmoteCancelStep>();
        for (var i = 0; i < ticks; i++)
            steps.Add(Step(plan, facts, frameMs));
        return steps;
    }

    /// <summary>The tick index (after the first interrupt at 0) that one retry wait ends on, at <see cref="FrameMs"/>.</summary>
    private const int RetryTick = (int)(EmoteCancelPlan.RetryAfterMs / FrameMs);

    private static List<int> Interrupts(IEnumerable<EmoteCancelStep> steps) =>
        steps.Where(s => s.Action == EmoteCancelAction.Interrupt).Select(s => s.Command).ToList();

    private static IEnumerable<EmoteCancelFacts> EveryFacts()
    {
        foreach (var ready in new[] { false, true })
        foreach (var locked in new[] { false, true })
        foreach (var sit in new[] { false, true })
        foreach (var loop in new[] { false, true })
        foreach (var position in new[] { false, true })
        foreach (var available in new[] { false, true })
            yield return Facts(ready, locked, sit, loop, position, available);
    }

    // ---------- 06-C1: the plan ----------

    [Fact]
    public void ANullCommandAddress_NeverProduces502Or503()
    {
        foreach (var facts in EveryFacts().Where(f => !f.CommandAvailable))
            Assert.Empty(Interrupts(Run(facts)));

        // Nor does it when the address goes null after the first interrupt: the retry isn't sent.
        var plan = new EmoteCancelPlan();
        plan.Request();
        Assert.Equal(EmoteCancelAction.Interrupt, Step(plan, Facts()).Action);
        var later = Steps(plan, Facts(commandAvailable: false));
        Assert.Empty(Interrupts(later));
        Assert.False(plan.Pending);
    }

    [Fact]
    public void ANullCommandAddress_DoesNothing_AndSaysSoOnce()
    {
        var steps = Run(Facts(commandAvailable: false));

        Assert.Equal(new EmoteCancelStep(EmoteCancelAction.None, Note: EmoteCancelNote.Unavailable), steps[0]);
        Assert.All(steps.Skip(1), s => Assert.Equal(EmoteCancelStep.Nothing, s));
        // The sit toggle doesn't need it.
        Assert.Equal(EmoteCancelAction.Sit, Run(Facts(sitPose: true, commandAvailable: false))[0].Action);
    }

    [Fact]
    public void NotInALoop_SendsNothing()
    {
        var plan = new EmoteCancelPlan();
        plan.Request();

        Assert.Equal(EmoteCancelStep.Nothing, Step(plan, Facts(inLoop: false, positionLoop: true)));
        Assert.False(plan.Pending);
        Assert.All(Run(Facts(inLoop: false)), s => Assert.Equal(EmoteCancelStep.Nothing, s));
    }

    [Fact]
    public void WithoutARequest_NothingIsSent()
    {
        var plan = new EmoteCancelPlan();

        foreach (var facts in EveryFacts())
            Assert.Equal(EmoteCancelStep.Nothing, Step(plan, facts));
        Assert.False(plan.Pending);
    }

    [Fact]
    public void Retries_AreBounded_ToTheOtherCodeOnce_ThenItGivesUp()
    {
        var steps = Run(Facts());

        Assert.Equal([EmoteCancelPlan.InterruptEmote, EmoteCancelPlan.InterruptEmoteSpecial], Interrupts(steps));
        Assert.Equal(EmoteCancelAction.Interrupt, steps[0].Action);
        Assert.Equal(EmoteCancelAction.Interrupt, steps[RetryTick].Action);
        Assert.All(steps.Skip(1).Take(RetryTick - 1), s => Assert.Equal(EmoteCancelStep.Nothing, s));
        var gaveUp = Assert.Single(steps, s => s.Note == EmoteCancelNote.GaveUp);
        Assert.Equal(steps[2 * RetryTick], gaveUp);
        Assert.All(steps.Skip(2 * RetryTick + 1), s => Assert.Equal(EmoteCancelStep.Nothing, s));
    }

    [Theory]
    [InlineData(4)] // 240 fps
    [InlineData(7)] // 144 fps
    [InlineData(33)] // 30 fps
    [InlineData(250)] // a hitch
    public void Retries_WaitAboutOneSecond_NotAFrameCount(long frameMs)
    {
        // 06-C3: the wait is wall time, so it's the same at any frame rate and longer than a server round trip.
        Assert.InRange(EmoteCancelPlan.RetryAfterMs, 800, 1500);
        var steps = Run(Facts(), ticks: (int)(3 * EmoteCancelPlan.RetryAfterMs / frameMs) + 2, frameMs: frameMs);

        var sent = steps.Select((s, i) => (s, i)).Where(x => x.s.Action == EmoteCancelAction.Interrupt).Select(x => x.i).ToList();
        Assert.Equal(2, sent.Count);
        var expected = (int)((EmoteCancelPlan.RetryAfterMs + frameMs - 1) / frameMs);
        Assert.Equal(expected, sent[1] - sent[0]);
        var gaveUp = steps.FindIndex(s => s.Note == EmoteCancelNote.GaveUp);
        Assert.Equal(expected, gaveUp - sent[1]);
    }

    [Fact]
    public void LeavingTheLoop_EndsThePlan_WithoutARetry()
    {
        var plan = new EmoteCancelPlan();
        plan.Request();
        Assert.Equal(EmoteCancelPlan.InterruptEmote, Step(plan, Facts()).Command);

        Assert.Equal(EmoteCancelStep.Nothing, Step(plan, Facts(inLoop: false)));
        Assert.False(plan.Pending);
        Assert.Empty(Interrupts(Steps(plan, Facts())));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SitPoses_GetTheSitToggle_Once(bool positionLoop)
    {
        foreach (var inLoop in new[] { false, true })
        {
            var steps = Run(Facts(sitPose: true, inLoop: inLoop, positionLoop: positionLoop));

            Assert.Equal(new EmoteCancelStep(EmoteCancelAction.Sit), steps[0]);
            Assert.All(steps.Skip(1), s => Assert.Equal(EmoteCancelStep.Nothing, s));
        }
    }

    [Fact]
    public void DozeLikePoses_Get503First_OtherLoops502First()
    {
        Assert.Equal([EmoteCancelPlan.InterruptEmoteSpecial, EmoteCancelPlan.InterruptEmote], Interrupts(Run(Facts(positionLoop: true))));
        Assert.Equal([EmoteCancelPlan.InterruptEmote, EmoteCancelPlan.InterruptEmoteSpecial], Interrupts(Run(Facts(positionLoop: false))));
        Assert.Equal(502, EmoteCancelPlan.InterruptEmote);
        Assert.Equal(503, EmoteCancelPlan.InterruptEmoteSpecial);
    }

    [Fact]
    public void BeingLockedAgain_DropsThePlan_SoTheLockEmoteIsNeverCancelled()
    {
        Assert.All(Run(Facts(locked: true)), s => Assert.Equal(EmoteCancelStep.Nothing, s));
        Assert.All(Run(Facts(locked: true, sitPose: true)), s => Assert.Equal(EmoteCancelStep.Nothing, s));

        // Re-locked after the first interrupt: no retry, even once unlocked again without a new request.
        var plan = new EmoteCancelPlan();
        plan.Request();
        Assert.Equal(EmoteCancelAction.Interrupt, Step(plan, Facts()).Action);
        Assert.Equal(EmoteCancelStep.Nothing, Step(plan, Facts(locked: true)));
        Assert.False(plan.Pending);
        Assert.Empty(Interrupts(Steps(plan, Facts())));
    }

    [Fact]
    public void NotReady_Waits_ThenActs()
    {
        var plan = new EmoteCancelPlan();
        plan.Request();

        for (var i = 0; i < 100; i++)
            Assert.Equal(EmoteCancelStep.Nothing, Step(plan, Facts(ready: false), ms: 50));
        Assert.True(plan.Pending);
        Assert.Equal(EmoteCancelPlan.InterruptEmote, Step(plan, Facts()).Command);

        // Time while zoning doesn't count toward the retry (06-C3: as frames didn't before): 200 ms ready, 5 s zoning,
        // then the retry comes after the remaining 800 ms of ready time. The first tick back adds nothing, as the gap
        // since the last ready tick was a zone.
        const int before = 50;
        for (var i = 0; i < before; i++)
            Assert.Equal(EmoteCancelStep.Nothing, Step(plan, Facts()));
        for (var i = 0; i < 100; i++)
            Assert.Equal(EmoteCancelStep.Nothing, Step(plan, Facts(ready: false), ms: 50));
        for (var i = 0; i < RetryTick - before; i++)
            Assert.Equal(EmoteCancelStep.Nothing, Step(plan, Facts()));
        Assert.Equal(EmoteCancelPlan.InterruptEmoteSpecial, Step(plan, Facts()).Command);
    }

    [Fact]
    public void CancelAndARepeatedRequest_StartOver()
    {
        var plan = new EmoteCancelPlan();
        plan.Request();
        plan.Cancel();
        Assert.False(plan.Pending);
        Assert.Equal(EmoteCancelStep.Nothing, Step(plan, Facts()));

        plan.Request();
        Assert.Equal(EmoteCancelPlan.InterruptEmote, Step(plan, Facts()).Command);
        plan.Request();
        Assert.Equal(EmoteCancelPlan.InterruptEmoteSpecial, Step(plan, Facts(positionLoop: true)).Command);
    }

    [Fact]
    public void GivingUp_IsLoggedOnce()
    {
        // The plan says so once per request, however long it is ticked afterwards ...
        var plan = new EmoteCancelPlan();
        for (var request = 0; request < 3; request++)
        {
            plan.Request();
            Assert.Single(Steps(plan, Facts()), s => s.Note == EmoteCancelNote.GaveUp);
            Assert.False(plan.Pending);
        }

        // ... and the controller logs only the first one, for the session: the flag is set there and never cleared.
        var source = LockSource;
        var step = ProfileSettingsTests.MethodBody(source, "    private void StepPoseCancel()");
        Assert.Contains(
            "else if (step.Note == EmoteCancelNote.GaveUp && !_loggedEmoteCancelGaveUp)\n        {\n            _loggedEmoteCancelGaveUp = true;\n            _log.Information(\"WindUpKey: the looping emote didn't end",
            step);
        Assert.Single(Regex.Matches(source, @"_loggedEmoteCancelGaveUp\s*=\s*true"));
        Assert.DoesNotMatch(new Regex(@"_loggedEmoteCancelGaveUp\s*=\s*false"), source);
        Assert.Single(Regex.Matches(source, @"the looping emote didn't end"));
    }

    [Fact]
    public void ThePoseDecidesTheSitToggle_NotTheConfiguredEmote()
    {
        // 06-C4: the plan never sees the configured lock emote, only the detected pose ...
        Assert.Equal(
            ["Ready", "Locked", "SitPose", "InLoop", "PositionLoop", "CommandAvailable"],
            typeof(EmoteCancelFacts).GetProperties().Select(p => p.Name));
        // ... so a doze with Ground Sit configured (no sit pose detected) gets 503, never a /sit toggle.
        var dozing = Run(Facts(sitPose: false, positionLoop: true));
        Assert.DoesNotContain(dozing, s => s.Action == EmoteCancelAction.Sit);
        Assert.Equal(EmoteCancelPlan.InterruptEmoteSpecial, Interrupts(dozing)[0]);

        // And the controller detects the pose from the game alone (three ways), never from the configured emote.
        var source = LockSource;
        var start = source.IndexOf("    private bool IsInSitPose() =>", StringComparison.Ordinal);
        Assert.True(start >= 0, "IsInSitPose not found");
        var sitPose = source[start..source.IndexOf(';', start)];
        Assert.Equal("    private bool IsInSitPose() =>\n        _condition[ConditionFlag.InThatPosition] && IsSittingOrGroundSitting()", sitPose);
        var sitting = ProfileSettingsTests.MethodBody(source, "    private unsafe bool IsSittingOrGroundSitting()");
        Assert.Contains("character->EmoteController.EmoteId", sitting);
        Assert.Contains("character->EmoteController.CurrentPoseType", sitting);
        Assert.Contains("character->Mode == CharacterModes.InPositionLoop && character->ModeParam is 1 or 2", sitting);
        foreach (var body in new[]
                 {
                     sitting,
                     ProfileSettingsTests.MethodBody(source, "    private EmoteCancelFacts ReadEmoteCancelFacts()"),
                     ProfileSettingsTests.MethodBody(source, "    private void StepPoseCancel()"),
                 })
        {
            Assert.DoesNotContain("_config", body);
            Assert.DoesNotContain("LockEmote", body);
        }
    }

    // ---------- 06-C1: hooks aren't kept alive for a pose cancel ----------

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void MovementHooks_AreNeededOnlyWhileLockedOrMuted(bool locked, bool inputMute, bool needed)
    {
        Assert.Equal(needed, MovementHookRule.Needed(locked, inputMute));
    }

    private static string LockSource => ProfileSettingsTests.PluginSource(Path.Combine("Services", "LockController.cs"));

    [Fact]
    public void TheNudge_IsGone_AndTheHookLifetimeUsesTheRule()
    {
        var source = LockSource;
        Assert.DoesNotContain("_nudgeForwardTicks", source);
        Assert.DoesNotContain("RequestCancelPoseNudge", source);
        Assert.DoesNotMatch(new Regex(@"\*sumForward\s*=\s*1"), source);

        var ensure = ProfileSettingsTests.MethodBody(source, "    private void EnsureHooksInstalled()");
        Assert.Contains("if (!MovementHookRule.Needed(_locked, _inputMute))", ensure);
        Assert.Contains("if (_hooksWereEligible || (_inputMute && !_locked))", ensure);

        var tick = ProfileSettingsTests.MethodBody(source, "    public void Tick()");
        Assert.Contains("if (_hooksInstalled && !MovementHookRule.Needed(_locked, _inputMute))\n        {\n            RemoveHooks();", tick);
        Assert.DoesNotContain("UninstallHooks()", tick);
    }

    [Fact]
    public void TheController_StepsThePlanFromTick_AndNeverCallsThroughANullAddress()
    {
        var source = LockSource;
        var tick = ProfileSettingsTests.MethodBody(source, "    public void Tick()");
        Assert.StartsWith("    public void Tick()\n    {\n        StepPoseCancel();", tick);

        var request = ProfileSettingsTests.MethodBody(source, "    public void RequestPoseCancel()");
        Assert.Contains("_emoteCancel.Request();", request);
        Assert.DoesNotContain("_commands.", request);

        // The one call site of ExecuteCommand, behind a null check of its own address in the same branch.
        var step = ProfileSettingsTests.MethodBody(source, "    private void StepPoseCancel()");
        Assert.Single(Regex.Matches(source, @"GameMain\.ExecuteCommand\("));
        var check = step.IndexOf("if (!ExecuteCommandAvailable)\n                    break;", StringComparison.Ordinal);
        var call = step.IndexOf("GameMain.ExecuteCommand(step.Command, 0, 0, 0, 0);", StringComparison.Ordinal);
        Assert.True(check >= 0 && call > check, "ExecuteCommand must be guarded by its address check");
        Assert.Contains("private static bool ExecuteCommandAvailable => (nint)GameMain.MemberFunctionPointers.ExecuteCommand != 0;", source);

        // IsInEmoteLoop is only called when its address is non-null; otherwise the old conditions decide.
        Assert.Single(Regex.Matches(source, @"IsInEmoteLoop\(\)"));
        Assert.Contains("if (EmoteLoopCheckAvailable)\n                    inLoop = character->EmoteController.IsInEmoteLoop()", source);
        Assert.Contains("private static bool EmoteLoopCheckAvailable => (nint)EmoteController.MemberFunctionPointers.IsInEmoteLoop != 0;", source);
        Assert.Contains("var conditionLoop = _condition[ConditionFlag.InThatPosition] || _condition[ConditionFlag.Emoting];", source);

        // The plan's retry runs on the monotonic millisecond clock (06-C3).
        Assert.Contains("_emoteCancel.Next(ReadEmoteCancelFacts(), Environment.TickCount64);", step);

        // Being locked again reaches the plan (which then drops itself), and the pose read doesn't happen.
        Assert.Contains("if (!ready || _locked)\n            return new EmoteCancelFacts(ready, _locked, SitPose: false,", source);

        // Logout and unload drop a pending cancel; dropping idle hooks mid-world doesn't.
        var uninstall = ProfileSettingsTests.MethodBody(source, "    public void UninstallHooks()");
        Assert.Contains("_emoteCancel.Cancel();", uninstall);
        Assert.DoesNotContain("_emoteCancel", ProfileSettingsTests.MethodBody(source, "    private void RemoveHooks()"));
    }

    [Fact]
    public void DozeLikePoses_CountAsInTheLoop_WhenTheLoopCheckExists()
    {
        // 06-C3: nothing confirms IsInEmoteLoop reports an InPositionLoop pose, so the mode counts too; without its
        // address the old conditions decide, unchanged (also when the read fails).
        var facts = ProfileSettingsTests.MethodBody(LockSource, "    private EmoteCancelFacts ReadEmoteCancelFacts()");
        Assert.Contains(
            "            if (character != null)\n            {\n                positionLoop = character->Mode == CharacterModes.InPositionLoop;\n                if (EmoteLoopCheckAvailable)\n                    inLoop = character->EmoteController.IsInEmoteLoop() || positionLoop;\n            }",
            facts);
        Assert.Contains("var inLoop = conditionLoop;", facts);
        Assert.Contains("_log.Warning(ex, \"WindUpKey: failed to read the emote loop state\");\n            inLoop = conditionLoop;", facts);
        Assert.Equal(3, Regex.Matches(facts, @"\binLoop\s*=").Count);
        Assert.Equal(2, Regex.Matches(facts, @"\binLoop = conditionLoop;").Count);
        // ... and such a pose then gets 503 first.
        Assert.Equal(EmoteCancelPlan.InterruptEmoteSpecial, Interrupts(Run(Facts(inLoop: true, positionLoop: true)))[0]);
    }

    [Fact]
    public void TheTimer_RequestsThePoseCancel_WhenTheDollIsWound()
    {
        var timer = ProfileSettingsTests.PluginSource(Path.Combine("Services", "WindTimerService.cs"));
        var sync = ProfileSettingsTests.MethodBody(timer, "    private void SyncLockState()");
        Assert.Contains("else if (becameUnlocked && _objects.LocalPlayer is not null)\n            _lock.RequestPoseCancel();", sync);
    }

    // ---------- 06-C6: /doze can't be the lock emote ----------

    private static string CommandRunnerSource => ProfileSettingsTests.PluginSource(Path.Combine("Services", "GameCommandRunner.cs"));

    [Fact]
    public void TheLockEmoteRule_ExcludesDozeAndOtherPositionLoops_ButNotTheSits()
    {
        // The Emote and EmoteMode sheets: Doze is row 13 with no EmoteMode (0); Sleep, row 88, is the doze loop
        // (EmoteMode 3, ConditionMode 11); Sit (50, EmoteMode 2) and Ground Sit (52, EmoteMode 1) have ConditionMode 11 too.
        Assert.False(LockEmoteRule.IsAllowed(LockEmoteRule.DozeEmoteId, 0, isSitOrGroundSit: false));
        Assert.False(LockEmoteRule.IsAllowed(LockEmoteRule.DozeEmoteId, 0, isSitOrGroundSit: true));
        Assert.False(LockEmoteRule.IsAllowed(88, LockEmoteRule.InPositionLoopConditionMode, isSitOrGroundSit: false));
        Assert.True(LockEmoteRule.IsAllowed(50, LockEmoteRule.InPositionLoopConditionMode, isSitOrGroundSit: true));
        Assert.True(LockEmoteRule.IsAllowed(CharacterProfile.GroundSitEmoteId, LockEmoteRule.InPositionLoopConditionMode, isSitOrGroundSit: true));
        // An ordinary looping emote (ConditionMode 3), and one without an EmoteMode, stay allowed.
        Assert.True(LockEmoteRule.IsAllowed(101, 3, isSitOrGroundSit: false));
        Assert.True(LockEmoteRule.IsAllowed(141, 0, isSitOrGroundSit: false));
        Assert.Equal(13, LockEmoteRule.DozeEmoteId);
        Assert.Equal(11, LockEmoteRule.InPositionLoopConditionMode);
    }

    [Fact]
    public void ExcludedEmotes_AreLeftOutOfTheLockEmoteList()
    {
        // The list behind the doll's combo and the owner's remote-settings combo skips them, before anything is added.
        var source = CommandRunnerSource;
        var list = ProfileSettingsTests.MethodBody(source, "    public IReadOnlyList<(ushort Id, string Name)> GetUnlockedLoopingEmotes()");
        var check = list.IndexOf("if (!LockEmoteRule.IsAllowed((ushort)emote.RowId, ConditionMode(emote), forceSit))\n                continue;", StringComparison.Ordinal);
        Assert.True(check >= 0 && check < list.IndexOf("list.Add((id, name));", StringComparison.Ordinal), "the list must skip excluded emotes");
        Assert.Contains("emote.EmoteMode.RowId == 0 ? (byte)0 : emote.EmoteMode.ValueNullable?.ConditionMode ?? 0;", source);

        // The always-added sit was row 13, which is Doze: it is Sit's row now, and both forced rows are allowed.
        Assert.Contains("public const ushort SitEmoteId = 50;", source);
        Assert.Contains("TryAddForcedSitEmote(sheet, list, seen, SitEmoteId, \"Sit\");", list);
        Assert.Contains("TryAddForcedSitEmote(sheet, list, seen, CharacterProfile.GroundSitEmoteId, \"Sit on Ground\");", list);
        Assert.True(LockEmoteRule.IsAllowed(50, LockEmoteRule.InPositionLoopConditionMode, isSitOrGroundSit: true));
        Assert.DoesNotMatch(new Regex(@"\b13\b"), ProfileSettingsTests.MethodBody(source, "    public IReadOnlyList<(ushort Id, string Name)> GetUnlockedLoopingEmotes()"));
    }

    [Fact]
    public void AStoredExcludedEmote_IsUsedAsGroundSit_AndKeptInTheConfig()
    {
        // At runtime it falls back like an emote that isn't unlocked ...
        var source = CommandRunnerSource;
        var usable = ProfileSettingsTests.MethodBody(source, "    private bool IsEmoteIdUnlockedAndLooping(ushort emoteId)");
        var check = usable.IndexOf("if (!LockEmoteRule.IsAllowed(emoteId, ConditionMode(emote), sit))\n            return false;", StringComparison.Ordinal);
        Assert.True(check >= 0 && check < usable.IndexOf("if (sit)\n            return true;", StringComparison.Ordinal), "excluded emotes must fall back before the sit shortcut");
        var resolve = ProfileSettingsTests.MethodBody(source, "    private ushort ResolveLockEmoteId()");
        Assert.Contains("if (id != CharacterProfile.GroundSitEmoteId && IsEmoteIdUnlockedAndLooping(id))\n            return id;\n\n        return CharacterProfile.GroundSitEmoteId;", resolve);
        Assert.DoesNotContain("LockEmoteId =", resolve + usable);

        // ... and nothing rewrites it: a profile or the legacy global value keeps Doze, so a downgrade still has it.
        var kept = new CharacterProfile { LockEmoteId = LockEmoteRule.DozeEmoteId, AutoGroundSit = true };
        var filled = new CharacterProfile();
        ProfileMigration.FillLockEmote([kept, filled], globalAutoGroundSit: true, globalLockEmoteId: LockEmoteRule.DozeEmoteId);
        Assert.Equal(LockEmoteRule.DozeEmoteId, kept.LockEmoteId);
        Assert.Equal(LockEmoteRule.DozeEmoteId, filled.LockEmoteId);
        Assert.DoesNotContain("LockEmoteRule", ProfileSettingsTests.PluginSource("Configuration.cs"));
        Assert.DoesNotContain("LockEmoteRule", ProfileSettingsTests.PluginSource("ProfileMigration.cs"));

        // The combo shows it as it is, with the Ground Sit note, since the list leaves it out.
        var view = LockEmoteDisplay.Resolve(LockEmoteRule.DozeEmoteId, [(50, "Sit"), (52, "Sit on Ground")], "Doze");
        Assert.Equal(LockEmoteRule.DozeEmoteId, view.SelectedId);
        Assert.False(view.Unlocked);
        Assert.EndsWith(LockEmoteDisplay.GroundSitFallbackNote, view.Preview);
    }

    [Fact]
    public void AnExcludedEmoteFromAnOwner_IsAppliedAsGroundSit_NotRefused()
    {
        Assert.Equal(CharacterProfile.GroundSitEmoteId, LockEmoteRule.FromRemote(LockEmoteRule.DozeEmoteId, allowed: false));
        Assert.Equal(CharacterProfile.GroundSitEmoteId, LockEmoteRule.FromRemote(88, allowed: false));
        Assert.Equal(CharacterProfile.GroundSitEmoteId, LockEmoteRule.FromRemote(0, allowed: true));
        Assert.Equal((ushort)141, LockEmoteRule.FromRemote(141, allowed: true));
        Assert.Equal((ushort)50, LockEmoteRule.FromRemote(50, allowed: true));

        // Both places an owner's settings land (the relay message and the debug self-owner path) use it.
        var relay = ProfileSettingsTests.PluginSource(Path.Combine("Services", "RelayClient.cs"));
        const string apply = "_config.LockEmoteId = LockEmoteRule.FromRemote(emoteId, _commands.IsAllowedLockEmote(emoteId));";
        Assert.Equal(2, Regex.Matches(relay, Regex.Escape(apply)).Count);
        Assert.Contains(apply, ProfileSettingsTests.MethodBody(relay, "    private async Task HandleInboundOwnerSettingsUpdateAsync(Envelope envelope, CancellationToken ct)"));
        Assert.Contains(apply, ProfileSettingsTests.MethodBody(relay, "    private void ApplyLocalOwnerSettingsUpdate("));
        Assert.DoesNotMatch(new Regex(@"_config\.LockEmoteId\s*=\s*emoteId\s*==\s*0"), relay);
        var allowed = ProfileSettingsTests.MethodBody(CommandRunnerSource, "    public bool IsAllowedLockEmote(ushort emoteId)");
        Assert.Contains("return LockEmoteRule.IsAllowed(emoteId, ConditionMode(emote), IsSitOrGroundSitEmote(emote, emote.Name.ToString()));", allowed);
    }

    // ---------- 06-C2: the self-test ----------

    [Fact]
    public void ThePostPatchJudge_CoversTheEmoteCancelAddresses()
    {
        const string whenZero = "emote cancel falls back to /sit, or stays posed";
        var missing = PostPatchJudge.Judge(new PostPatchFacts
        {
            Addresses =
            [
                new AddressFact("P-addr-execute-command", "GameMain.ExecuteCommand", false, whenZero),
                new AddressFact("P-addr-emote-loop", "EmoteController.IsInEmoteLoop", false),
            ],
        }).ToDictionary(r => r.Id);
        Assert.Equal(CheckStatus.Fail, missing["P-addr-execute-command"].Status);
        Assert.Contains(whenZero, missing["P-addr-execute-command"].Reason);
        Assert.Equal(CheckStatus.Fail, missing["P-addr-emote-loop"].Status);
        Assert.Contains("ClientStructs doesn't know it this patch", missing["P-addr-emote-loop"].Reason);
        Assert.Equal(CheckStatus.Manual, missing["P-emote-cancel"].Status);

        var present = PostPatchJudge.Judge(new PostPatchFacts
        {
            Addresses =
            [
                new AddressFact("P-addr-execute-command", "GameMain.ExecuteCommand", true, whenZero),
                new AddressFact("P-addr-emote-loop", "EmoteController.IsInEmoteLoop", true),
            ],
        }).ToDictionary(r => r.Id);
        Assert.Equal(CheckStatus.Pass, present["P-addr-execute-command"].Status);
        Assert.Equal(CheckStatus.Pass, present["P-addr-emote-loop"].Status);
    }

    [Fact]
    public void TheCatalog_HasTheEmoteCancelChecks()
    {
        Assert.NotNull(SelfTestCatalog.PostPatch.SingleOrDefault(e => e.Id == "P-addr-execute-command"));
        Assert.NotNull(SelfTestCatalog.PostPatch.SingleOrDefault(e => e.Id == "P-addr-emote-loop"));
        var manual = Assert.Single(SelfTestCatalog.PostPatch, e => e.Id == "P-emote-cancel");
        var steps = string.Join(" ", manual.ManualSteps);
        foreach (var pose in new[] { "groundsit", "chair sit", "/doze", "another looping emote", "\"1 spring\" on Yourself (debug)", "without moving" })
            Assert.Contains(pose, steps);
        Assert.NotNull(SelfTestCatalog.Find("X-lock-emote"));
    }

    [Fact]
    public void TheSelfTestHost_ReadsBothAddresses_AndNeverSendsTheInterrupt()
    {
        var host = ProfileSettingsTests.PluginSource(Path.Combine("SelfTest", "Adapters", "SelfTestHost.cs"));
        Assert.Contains("new AddressFact(\"P-addr-execute-command\", \"GameMain.ExecuteCommand\", (nint)GameMain.MemberFunctionPointers.ExecuteCommand != 0,\n                    \"emote cancel falls back to /sit, or stays posed\"),", host);
        Assert.Contains("new AddressFact(\"P-addr-emote-loop\", \"EmoteController.IsInEmoteLoop\", (nint)CSEmoteController.MemberFunctionPointers.IsInEmoteLoop != 0,", host);

        // Read only (06-C2, 04-D8): nothing under SelfTest/ calls either function or sends 502/503.
        var selfTestDir = ProfileSettingsTests.PluginPath("SelfTest");
        foreach (var file in Directory.EnumerateFiles(selfTestDir, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotMatch(new Regex(@"ExecuteCommand\s*\("), text);
            Assert.DoesNotMatch(new Regex(@"IsInEmoteLoop\s*\("), text);
            Assert.DoesNotMatch(new Regex(@"\b50[23]\b"), text);
            Assert.DoesNotContain("EmoteCancelPlan", text);
            Assert.DoesNotContain("RequestPoseCancel", text);
        }
    }

    [Fact]
    public void TheLockEmoteCheck_WarnsWhenTheEmoteIsNotUnlocked()
    {
        var snapshot = new SessionSnapshot { LockEmoteId = 88, LockEmoteUnlocked = false };
        Assert.Equal(CheckStatus.Warn, Judge(snapshot).Status);
        Assert.Contains("Ground Sit", Judge(snapshot).Reason);
        Assert.Equal(CheckStatus.Pass, Judge(snapshot with { LockEmoteUnlocked = true }).Status);
        Assert.Equal(CheckStatus.Skip, Judge(snapshot with { LockEmoteUnlocked = null }).Status);

        // 06-C3: without a character (the title screen) it skips, and the emote list (and its shared cache) isn't read.
        var host = ProfileSettingsTests.PluginSource(Path.Combine("SelfTest", "Adapters", "SelfTestHost.cs"));
        Assert.Contains(
            "LockEmoteUnlocked = config.IsDoll && config.AutoGroundSit && objectTable.LocalPlayer is not null\n                ? LockEmoteDisplay.Resolve(config.EffectiveLockEmoteId, commands.GetUnlockedLoopingEmotes(), null).Unlocked\n                : null,",
            host);
        Assert.Single(Regex.Matches(host, @"GetUnlockedLoopingEmotes\("));

        static CheckResult Judge(SessionSnapshot s) => SessionFactsJudge.Judge(s).Single(r => r.Id == "X-lock-emote");
    }
}
