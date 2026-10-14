using System.Text.RegularExpressions;
using WindUpKey.Protocol;
using WindUpKey.SelfTest;
using WindUpKey.Services;

namespace WindUpKey.Tests;

/// <summary>
/// Issue 06 Part B: the safeword also pauses remote control (06-B1). The rules are in <see cref="SafewordPause"/>;
/// <c>RelayClient</c>, <c>WindTimerService</c>, <c>Plugin</c>, <c>Configuration</c> and <c>ConfigWindow</c> need Dalamud,
/// so the checks on their wiring read their source.
/// </summary>
public sealed class SafewordPauseTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    // ---------- which messages a pause refuses ----------

    [Theory]
    [InlineData(MessageTypes.Unwind)]
    [InlineData(MessageTypes.Call)]
    [InlineData(MessageTypes.OwnerSettingsUpdate)]
    public void Pause_RefusesEachRemoteControlMessage(string type)
    {
        Assert.True(SafewordPause.Refuses(type));
    }

    [Theory]
    [InlineData(MessageTypes.Wind)]
    [InlineData(MessageTypes.OwnerSettingsQuery)]
    [InlineData(MessageTypes.PresenceQuery)]
    [InlineData(MessageTypes.PresenceResult)]
    [InlineData(MessageTypes.WindRequest)]
    [InlineData(MessageTypes.PairEstablished)]
    [InlineData(MessageTypes.PairRemove)]
    [InlineData(MessageTypes.PairSync)]
    [InlineData(MessageTypes.OwnerGrant)]
    [InlineData(MessageTypes.OwnerRevoked)]
    [InlineData(MessageTypes.Ping)]
    [InlineData(null)]
    public void Pause_LetsWindingQueriesPresenceAndPairingThrough(string? type)
    {
        Assert.False(SafewordPause.Refuses(type));
    }

    [Theory]
    [InlineData("    private async Task HandleInboundUnwindAsync(", "        _timer.ClearWind(winderIdentity);")]
    [InlineData("    private async Task HandleInboundCallAsync(", "        await _callTravel.RequestAsync(payload).ConfigureAwait(false);")]
    [InlineData("    private async Task HandleInboundOwnerSettingsUpdateAsync(", "        if (payload.MaxWindHours is { } hours)")]
    public void Pause_EachRefusedHandlerChecksItWhenHandled_AfterItsPermission_BeforeApplyingAnything(string handler, string firstEffect)
    {
        var body = ProfileSettingsTests.MethodBody(RelayClientSource, handler);
        const string check = "        if (await RefuseIfSafewordPausedAsync(envelope.Type, payload.RequestId, fromKey, ct).ConfigureAwait(false))\n            return;";
        var at = body.IndexOf(check, StringComparison.Ordinal);
        Assert.True(at >= 0, $"{handler.Trim()} doesn't check the pause");
        Assert.Single(Regex.Matches(body, Regex.Escape("RefuseIfSafewordPausedAsync(")));

        // Unpaired senders still get no reply, and a partner without the right gets not_allowed, before the pause shows.
        var stealth = body.IndexOf("IsPairedByKey(fromKey)", StringComparison.Ordinal);
        var permission = body.IndexOf("Code = ErrorCodes.NotAllowed,", StringComparison.Ordinal);
        Assert.True(stealth >= 0 && stealth < permission && permission < at, $"{handler.Trim()}: the pause is checked too early");

        var effect = body.IndexOf(firstEffect, StringComparison.Ordinal);
        Assert.True(effect > at, $"{handler.Trim()}: something is applied before the pause is checked");
    }

    [Theory]
    [InlineData("    private async Task HandleInboundWindAsync(")]
    [InlineData("    private async Task HandleInboundOwnerSettingsQueryAsync(")]
    [InlineData("    private async Task HandleInboundPresenceQueryAsync(")]
    [InlineData("    private void HandleInboundWindRequest(")]
    [InlineData("    private void HandlePairSync(")]
    public void Pause_WindingQueriesPresenceAndPairingHandlers_NeverCheckIt(string handler)
    {
        var body = ProfileSettingsTests.MethodBody(RelayClientSource, handler);
        Assert.DoesNotContain("RefuseIfSafewordPausedAsync", body);
        Assert.DoesNotContain("IsRemoteControlPaused", body);
    }

    [Fact]
    public void Pause_TheRefusal_RepliesSafewordPausedToTheSender_WithTheRequestId()
    {
        var helper = ProfileSettingsTests.MethodBody(RelayClientSource, "    private async Task<bool> RefuseIfSafewordPausedAsync(");
        Assert.Contains("if (!SafewordPause.Refuses(messageType) || !_timer.IsRemoteControlPaused)\n            return false;", helper);
        Assert.Contains("RequestId = requestId,", helper);
        Assert.Contains("To = fromKey,", helper);
        Assert.Contains("Code = ErrorCodes.SafewordPaused,", helper);
        Assert.Contains("return true;", helper);
        // Exactly the three refused handlers call it.
        Assert.Equal(3, Regex.Matches(RelayClientSource, Regex.Escape("await RefuseIfSafewordPausedAsync(envelope.Type,")).Count);

        // The pause is read from the active character's own stored value, with the clock and Hardcore, each time.
        Assert.Contains(
            "SafewordPause.IsActive(_config.SafewordPausedUntilUtc, DateTimeOffset.UtcNow, _config.HardcoreMode);",
            TimerSource);
    }

    // ---------- how long it lasts ----------

    [Fact]
    public void Extend_StartsAPauseOfTheSafewordHours()
    {
        Assert.Equal(Now.AddHours(3), SafewordPause.Extend(null, Now, 3));
        Assert.Equal(Now.AddHours(3), SafewordPause.Extend(Now.AddHours(-1), Now, 3));
        Assert.Equal(Now.AddHours(2), SafewordPause.Extend(Now.AddMinutes(30), Now, 2));
    }

    [Fact]
    public void Extend_NeverShortensALongerPause()
    {
        Assert.Equal(Now.AddHours(20), SafewordPause.Extend(Now.AddHours(20), Now, 1));

        foreach (var currentHours in new double?[] { null, -5, 0, 0.25, 1, 12, 23.9, 24, 30, 500 })
        foreach (var hours in new double[] { 1, 6, 24 })
        {
            var current = currentHours is { } h ? Now.AddHours(h) : (DateTimeOffset?)null;
            var extended = SafewordPause.Extend(current, Now, hours);
            Assert.True(extended >= Now.AddHours(hours), $"{currentHours}h + {hours}h: {extended}");
            if (SafewordPause.Normalize(current, Now) is { } counted)
                Assert.True(extended >= counted, $"{currentHours}h + {hours}h shortened to {extended}");
            Assert.True(extended <= Now + SafewordPause.MaxLength, $"{currentHours}h + {hours}h: {extended}");
        }
    }

    [Fact]
    public void Extend_KeepsTheHoursWithinTheSetting()
    {
        Assert.Equal(Now.AddHours(1), SafewordPause.Extend(null, Now, 0));
        Assert.Equal(Now.AddHours(24), SafewordPause.Extend(null, Now, 100));
    }

    [Fact]
    public void AnEndedPause_DoesNotBlock()
    {
        Assert.False(SafewordPause.IsActive(null, Now, hardcore: false));
        Assert.False(SafewordPause.IsActive(Now.AddSeconds(-1), Now, hardcore: false));
        Assert.False(SafewordPause.IsActive(Now, Now, hardcore: false));
        Assert.True(SafewordPause.IsActive(Now.AddSeconds(1), Now, hardcore: false));
        Assert.Null(SafewordPause.Normalize(Now.AddHours(-3), Now));
        Assert.Null(SafewordPause.EndsAt(Now, Now, hardcore: false));
    }

    [Fact]
    public void AFarFutureStamp_IsClampedToADay()
    {
        // A clock that jumped back, or an edited config: the pause still ends at most 24 h from now.
        Assert.Equal(Now.AddHours(24), SafewordPause.Normalize(Now.AddDays(30), Now));
        Assert.Equal(Now.AddHours(24), SafewordPause.EndsAt(Now.AddHours(24).AddTicks(1), Now, hardcore: false));
        Assert.Equal(Now.AddHours(24), SafewordPause.Normalize(Now.AddHours(24), Now));
        Assert.Equal(Now.AddHours(5), SafewordPause.Normalize(Now.AddHours(5), Now));
        Assert.Equal(Now.AddHours(24), SafewordPause.Extend(Now.AddDays(30), Now, 1));
        Assert.True(SafewordPause.IsActive(Now.AddDays(30), Now, hardcore: false));
    }

    [Fact]
    public void StoredPauses_AreTidiedOnLoad_AndWhileRunning()
    {
        // ApplyProfile (a switch) and Migrate (a reload as the same character, which applies no profile) normalise it...
        var apply = ProfileSettingsTests.MethodBody(ConfigurationSource, "public void ApplyProfile(CharacterProfile profile)");
        Assert.Contains("SafewordPausedUntilUtc = SafewordPause.Normalize(profile.SafewordPausedUntilUtc, DateTimeOffset.UtcNow);", apply);
        var migrate = ProfileSettingsTests.MethodBody(ConfigurationSource, "public void Migrate()");
        Assert.Contains("SafewordPausedUntilUtc = SafewordPause.Normalize(SafewordPausedUntilUtc, DateTimeOffset.UtcNow);", migrate);

        // ...and the doll's tick clears an ended one and clamps one a clock jump put too far ahead, saving only a change.
        var tick = ProfileSettingsTests.MethodBody(TimerSource, "    public void Tick()");
        Assert.Contains("TidySafewordPause();", tick);
        var tidy = ProfileSettingsTests.MethodBody(TimerSource, "    private void TidySafewordPause()");
        Assert.Contains("var tidy = SafewordPause.EndsAt(stored, DateTimeOffset.UtcNow, _config.HardcoreMode);", tidy);
        Assert.Contains("if (tidy == stored)\n            return;", tidy);
    }

    // ---------- ending it ----------

    [Fact]
    public void Resume_ClearsThePause()
    {
        var resume = ProfileSettingsTests.MethodBody(TimerSource, "    public bool ResumeRemoteControl()");
        Assert.Contains("_config.SafewordPausedUntilUtc = null;\n            _config.Save();", resume);

        // The config button and the command both end it through that method.
        var ui = ProfileSettingsTests.MethodBody(ConfigWindowSource, "    private void DrawSafewordPause()");
        Assert.Contains("if (_timer.SafewordPauseEndsAt is not { } pauseEnd)", ui);
        Assert.Contains("if (ImGui.Button(\"End pause now\"))\n            _timer.ResumeRemoteControl();", ui);
        Assert.Contains("pauseEnd.ToLocalTime()", ui);
        Assert.Contains("DrawSafewordPause();", ProfileSettingsTests.MethodBody(ConfigWindowSource, "    private void DrawSafewordTab()"));
        Assert.Contains("if (SafewordPause.IsResume(rest, safewordUsable, Configuration.Safeword))", SafewordCommand);
        Assert.Contains("if (_timer.ResumeRemoteControl())", SafewordCommand);
    }

    [Theory]
    [InlineData("resume", true, "pineapple", true)]
    [InlineData("  Resume ", true, "pineapple", true)]
    [InlineData("RESUME", false, "pineapple", true)]
    [InlineData("resume", false, "resume", true)]
    [InlineData("resume", true, "resume", false)]
    [InlineData("pineapple", true, "pineapple", false)]
    [InlineData("resume now", true, "pineapple", false)]
    [InlineData("", true, "pineapple", false)]
    public void Resume_TheCommand_UnlessItIsHerUsableSafeword(string rest, bool usable, string stored, bool expected)
    {
        // Her safeword wins: using it favours the doll, and the config button still ends the pause.
        Assert.Equal(expected, SafewordPause.IsResume(rest, usable, stored));
    }

    [Fact]
    public void Hardcore_ClearsThePause()
    {
        Assert.False(SafewordPause.IsActive(Now.AddHours(5), Now, hardcore: true));
        Assert.Null(SafewordPause.EndsAt(Now.AddHours(5), Now, hardcore: true));

        // Turning it on clears it, and a profile or config in Hardcore never keeps one.
        var tab = ProfileSettingsTests.MethodBody(ConfigWindowSource, "    private void DrawSafewordTab()");
        var confirm = tab[tab.IndexOf("if (ImGui.Button(\"Confirm Hardcore\"))", StringComparison.Ordinal)..];
        Assert.Contains("_config.HardcoreMode = true;", confirm);
        Assert.Contains("_config.SafewordPausedUntilUtc = null;", confirm[..confirm.IndexOf("_config.Save();", StringComparison.Ordinal)]);
        foreach (var member in new[] { "public void ApplyProfile(CharacterProfile profile)", "public void Migrate()" })
        {
            var body = ProfileSettingsTests.MethodBody(ConfigurationSource, member);
            var hardcore = body[body.LastIndexOf("if (HardcoreMode)", StringComparison.Ordinal)..];
            Assert.Contains("SafewordPausedUntilUtc = null;", hardcore[..hardcore.IndexOf('}')]);
        }
    }

    // ---------- using the safeword ----------

    [Fact]
    public void SafewordWind_IgnoresTheCap()
    {
        // MaxWindHours 12, 10 h left: the safeword's 5 h all count.
        var wind = WindMath.Add(Now, Now.AddHours(10), TimeSpan.FromHours(5), maxWindHours: null);

        Assert.Equal(Now.AddHours(15), wind.Expiry);
        Assert.Equal(TimeSpan.FromHours(5), wind.Added);
    }

    [Fact]
    public void SafewordWind_NeverShortensTheTimer()
    {
        foreach (var currentHours in new double[] { -3, 0, 1, 72, 500 })
        foreach (var hours in new double[] { 1, 24 })
        {
            var current = Now.AddHours(currentHours);
            var floor = current > Now ? current : Now;
            Assert.Equal(floor.AddHours(hours), WindMath.Add(Now, current, TimeSpan.FromHours(hours), maxWindHours: null).Expiry);
        }
    }

    [Fact]
    public void UsingTheSafeword_PausesWindsPastTheCapAndCancelsTheCall()
    {
        var use = ProfileSettingsTests.MethodBody(TimerSource, "    public void UseSafeword(double hours)");
        Assert.Contains("_config.SafewordPausedUntilUtc = SafewordPause.Extend(_config.SafewordPausedUntilUtc, DateTimeOffset.UtcNow, hours);", use);
        Assert.Contains("AddWind(TimeSpan.FromHours(hours), ignoreMaxCap: true);", use);
        Assert.True(
            use.IndexOf("SafewordPause.Extend(", StringComparison.Ordinal) < use.IndexOf("AddWind(", StringComparison.Ordinal),
            "the pause is set after the wind");

        var command = SafewordCommand;
        var match = command.IndexOf("if (!SafewordText.Matches(rest, Configuration.Safeword))", StringComparison.Ordinal);
        var cancel = command.IndexOf("#if WINDUP_TESTING\n            _relay.CancelActiveCall();\n#endif", StringComparison.Ordinal);
        var used = command.IndexOf("_timer.UseSafeword(Configuration.SafewordHours);", StringComparison.Ordinal);
        Assert.True(match >= 0 && match < used && cancel >= 0, "the safeword doesn't pause and cancel the Call after matching");
        // 06-B3: wind before cancelling the Call, so she's never briefly unwound (re-locked, sat down, then stood up again).
        Assert.True(used < cancel, "the Call is cancelled before the safeword winds");
        Assert.DoesNotContain("_timer.AddHours(", command);
    }

    [Fact]
    public void TheSafeword_IsNeverLogged()
    {
        Assert.DoesNotMatch(@"\bLog\.|_log\.", SafewordCommand);
        Assert.DoesNotMatch(@"\bLog\.|_log\.", ProfileSettingsTests.PluginSource(Path.Combine("Services", "SafewordPause.cs")));
        Assert.DoesNotMatch(@"\bLog\.|_log\.", ProfileSettingsTests.MethodBody(TimerSource, "    public void UseSafeword(double hours)"));
        Assert.DoesNotMatch(@"\b_log\.", ProfileSettingsTests.MethodBody(RelayClientSource, "    private async Task<bool> RefuseIfSafewordPausedAsync("));
    }

    // ---------- the wire code ----------

    [Fact]
    public void SafewordPaused_IsAPeerCode_NeverRelayOnly()
    {
        Assert.Equal("safeword_paused", ErrorCodes.SafewordPaused);
        Assert.False(ErrorCodes.IsRelayOnly("safeword_paused"));
        Assert.False(ErrorCodes.IsRelayOnly(ErrorCodes.SafewordPaused));

        // The owner's client handles it as an ordinary routed error, never a session signal.
        Assert.Equal(RelaySessionSignal.None, RelayConnectPolicy.ClassifyError(ErrorCodes.SafewordPaused, "DOLLKEY1"));
        Assert.Equal(RelaySessionSignal.None, RelayConnectPolicy.ClassifyError(ErrorCodes.SafewordPaused, null));

        // A short code within the 03 limits: the relay routes it as it is (it needs no change).
        Assert.True(WireLimits.IsValid(new ErrorPayload
        {
            RequestId = Guid.NewGuid().ToString("N"),
            To = "OWNERKY1",
            Code = ErrorCodes.SafewordPaused,
            Message = RelayClientSafewordPausedFailure,
        }));
    }

    [Fact]
    public void TheOwner_SeesThePauseForAnUnwindOrSettingsChange_WhileACallStaysSilent()
    {
        Assert.Equal("That doll has paused remote control for now.", RelayClientSafewordPausedFailure);
        var handler = ProfileSettingsTests.MethodBody(RelayClientSource, "    private async Task HandleErrorAsync(");
        Assert.Contains(": payload.Code == ErrorCodes.SafewordPaused\n                                ? SafewordPausedFailure", handler);
        Assert.Contains("if (payload.Code == ErrorCodes.SafewordPaused)\n        {\n            _notifier.NotifyWinderError(SafewordPausedFailure);\n            return;\n        }", handler);

        // A pending Call is taken (silently) before either.
        var call = handler.IndexOf("if (TryTakePendingCall(payload.RequestId))\n                return;", StringComparison.Ordinal);
        Assert.True(call >= 0 && call < handler.IndexOf("SafewordPausedFailure", StringComparison.Ordinal));
    }

    [Fact]
    public void TheOwner_AfterAPausedSettingsChange_TheWindowShowsWhatsActuallySet()
    {
        // 06-B3: a safeword_paused refusal of a settings change restores the last confirmed snapshot in the window
        // (08-T6: then re-queries the doll, keeping the message; see SmallFixesSourceTests).
        var handler = ProfileSettingsTests.MethodBody(RelayClientSource, "    private async Task HandleErrorAsync(");
        Assert.Contains(
            "if (payload.Code == ErrorCodes.SafewordPaused)\n                {\n                    var requery = !OwnerSettingsReQueryPending(dollKey);\n                    RestoreOwnerSettingsAfterRefusal(dollKey, message, keepErrorOnNextResult: true);",
            handler);
        Assert.Contains("}\n                else\n                    SetOwnerSettingsError(dollKey, message);", handler);

        var restore = ProfileSettingsTests.MethodBody(RelayClientSource, "    private void RestoreOwnerSettingsAfterRefusal(");
        Assert.Contains("snap.LastError = message;", restore);
        Assert.Contains("snap.RestoreSeq = ++_ownerSettingsRestoreSeq;", restore);
        Assert.DoesNotMatch(@"snap\.(MaxWindHours|AutoGroundSit|LockEmoteId|SettingsLocked|HasData)\s*=", restore);
        Assert.Contains("RestoreSeq = snap.RestoreSeq,",
            ProfileSettingsTests.MethodBody(RelayClientSource, "    private static OwnerSettingsSnapshot CloneOwnerSettings("));

        // The snapshot holds the last confirmed values: sending a change never writes it (only a result or an ack does).
        var update = ProfileSettingsTests.MethodBody(RelayClientSource, "    public async Task UpdateOwnerSettingsAsync(");
        var remote = update[update.IndexOf("if (!IsConnected)", StringComparison.Ordinal)..];
        Assert.DoesNotContain("_ownerSettingsByDollKey", remote);
        Assert.DoesNotContain("ApplyLocalOwnerSettingsSnapshot", remote);

        // The window drops its draft (the refused values) when the snapshot is restored.
        var window = ProfileSettingsTests.MethodBody(ConfigWindowSource, "    private void DrawPairOwnerWindow(");
        var resync = window.IndexOf("if (!draft.Synced || draft.RestoreSeq != snap.RestoreSeq)\n        {", StringComparison.Ordinal);
        Assert.True(resync >= 0, "the window doesn't resync its draft after a refusal");
        var block = window[resync..window.IndexOf("\n        }\n", resync, StringComparison.Ordinal)];
        Assert.Contains("draft.MaxHours = snap.MaxWindHours;", block);
        Assert.Contains("draft.RestoreSeq = snap.RestoreSeq;", block);
    }

    [Fact]
    public void TheDebugSelfOwnerSettingsChange_RespectsThePause()
    {
        // 06-B3: like self-unwind and self-Call, her own debug settings change is refused while paused, before applying.
        var update = ProfileSettingsTests.MethodBody(RelayClientSource, "    public async Task UpdateOwnerSettingsAsync(");
        var self = update.IndexOf("StringComparison.Ordinal))\n        {", StringComparison.Ordinal);
        var check = update.IndexOf(
            "            if (_timer.IsRemoteControlPaused)\n            {\n                ApplyLocalOwnerSettingsSnapshot(key);\n                RestoreOwnerSettingsAfterRefusal(key, SafewordPausedFailure);\n                _notifier.NotifyWinderError(SafewordPausedFailure);\n                return;\n            }",
            StringComparison.Ordinal);
        var apply = update.IndexOf("ApplyLocalOwnerSettingsUpdate(", StringComparison.Ordinal);
        Assert.True(self >= 0 && self < check && check < apply, "the debug self path applies a change while paused");
        Assert.True(apply < update.IndexOf("if (!IsConnected)", StringComparison.Ordinal));
    }

    // ---------- self-test ----------

    [Fact]
    public void SelfTest_JudgesThePauseState()
    {
        static CheckResult Judge(DateTimeOffset? until, bool hardcore = false) =>
            SessionFactsJudge.Judge(new SessionSnapshot { SafewordPausedUntilUtc = until, HardcoreMode = hardcore, TakenAtUtc = Now })
                .Single(r => r.Id == "X-safeword-pause");

        Assert.Equal(CheckStatus.Pass, Judge(null).Status);
        Assert.Equal(CheckStatus.Pass, Judge(null, hardcore: true).Status);
        Assert.Equal(CheckStatus.Pass, Judge(Now.AddHours(3)).Status);
        Assert.Contains("3 more hour", Judge(Now.AddHours(3)).Reason);
        Assert.Equal(CheckStatus.Pass, Judge(Now.AddHours(24)).Status);
        Assert.Equal(CheckStatus.Pass, Judge(Now.AddHours(-1)).Status);
        Assert.Equal(CheckStatus.Fail, Judge(Now.AddHours(24).AddMinutes(1)).Status);
        Assert.Equal(CheckStatus.Fail, Judge(Now.AddHours(3), hardcore: true).Status);
        Assert.NotNull(SelfTestCatalog.Find("X-safeword-pause"));
        Assert.Contains("SafewordPausedUntilUtc = config.SafewordPausedUntilUtc,",
            ProfileSettingsTests.PluginSource(Path.Combine("SelfTest", "Adapters", "SelfTestHost.cs")));
    }

    // ---------- source helpers ----------

    private static string RelayClientSource => ProfileSettingsTests.PluginSource(Path.Combine("Services", "RelayClient.cs"));

    private static string TimerSource => ProfileSettingsTests.PluginSource(Path.Combine("Services", "WindTimerService.cs"));

    private static string ConfigurationSource => ProfileSettingsTests.PluginSource("Configuration.cs");

    private static string ConfigWindowSource => ProfileSettingsTests.PluginSource(Path.Combine("Ui", "ConfigWindow.cs"));

    /// <summary>The <c>/windup safeword</c> branch of <c>Plugin.OnCommand</c>.</summary>
    private static string SafewordCommand
    {
        get
        {
            var plugin = ProfileSettingsTests.PluginSource("Plugin.cs");
            var start = plugin.IndexOf("if (string.Equals(verb, \"safeword\", StringComparison.OrdinalIgnoreCase))", StringComparison.Ordinal);
            Assert.True(start >= 0, "the safeword verb not found");
            var end = plugin.IndexOf("PluginChat.PrintError(ChatGui, \"Unknown command.", start, StringComparison.Ordinal);
            Assert.True(end > start, "end of the safeword verb not found");
            return plugin[start..end];
        }
    }

    /// <summary>RelayClient's owner-side text (the class needs Dalamud, so its constant is read from the source).</summary>
    private static string RelayClientSafewordPausedFailure =>
        Regex.Match(RelayClientSource, "public const string SafewordPausedFailure = \"([^\"]*)\";").Groups[1].Value;
}
