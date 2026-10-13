using WindUpKey.Services;
using WindUpKey.Ui;

namespace WindUpKey.Tests;

/// <summary>
/// Issue 06 Part A, a character switch: the low-wind tracker forgets the old character (06-A2), confirmations belong to
/// the character (and partner) they were opened for (06-A7), and a relay message queued for the old character is
/// dropped (06-A9).
/// </summary>
public sealed class CharacterSwitchTests
{
    private const string Main = "004000174AA8BCC2";
    private const string Alt = "0040001700000001";
    private const string Partner = "PARTNR22";

    // ---------- 06-A2: the low-wind profile guard ----------

    [Fact]
    public void LowWind_OnTheSameProfile_KeepsTheLastRemaining()
    {
        var guard = new LowWindProfileGuard();
        Assert.Null(guard.Filter(Main, null));

        Assert.Equal(TimeSpan.FromHours(5), guard.Filter(Main, TimeSpan.FromHours(5)));
    }

    [Fact]
    public void LowWind_AfterASwitch_ForgetsTheOldCharactersRemaining()
    {
        // Wound main (5 h seen last) to an expired alt: the alt's first tick has nothing to compare with, so the
        // "remaining went from >0 to empty" expiry echo can't fire.
        var guard = new LowWindProfileGuard();
        var last = guard.Filter(Main, null);
        last = TimeSpan.FromHours(5);

        last = guard.Filter(Alt, last);

        Assert.Null(last);
        var wouldEchoExpired = last is { } previous && previous > TimeSpan.Zero;
        Assert.False(wouldEchoExpired);
        // The alt is tracked now: its own next value passes.
        Assert.Equal(TimeSpan.Zero, guard.Filter(Alt, TimeSpan.Zero));
    }

    [Fact]
    public void LowWind_SwitchingBack_IsAFirstObservationAgain()
    {
        var guard = new LowWindProfileGuard();
        guard.Filter(Main, null);
        guard.Filter(Alt, TimeSpan.FromHours(5));

        Assert.Null(guard.Filter(Main, TimeSpan.FromHours(2)));
        Assert.Equal(TimeSpan.FromHours(2), guard.Filter(Main, TimeSpan.FromHours(2)));
    }

    [Fact]
    public void LowWind_TheFirstObservation_NeverCarriesAValue()
    {
        Assert.Null(new LowWindProfileGuard().Filter(Main, TimeSpan.FromHours(5)));
        Assert.Null(new LowWindProfileGuard().Filter(string.Empty, TimeSpan.FromHours(5)));
    }

    [Fact]
    public void LowWind_TheServiceFiltersBeforeEveryUseOfTheLastRemaining()
    {
        var service = ProfileSettingsTests.PluginSource(Path.Combine("Services", "LowWindWarningService.cs"));
        Assert.Contains("_lastRemaining = _profileGuard.Filter(_config.ActiveContentId, _lastRemaining);", service);
        foreach (var member in new[] { "    public void Tick()", "    public void OnLoggedIn()", "    public void OnWindChanged()", "    public void OnCleared(" })
        {
            var body = ProfileSettingsTests.MethodBody(service, member);
            var filter = body.IndexOf("ForgetOtherProfile();", StringComparison.Ordinal);
            var use = body.IndexOf("_lastRemaining", StringComparison.Ordinal);
            var remaining = body.IndexOf("TryGetRemaining(", StringComparison.Ordinal);
            Assert.True(filter >= 0, $"{member.Trim()} doesn't filter");
            Assert.True(use < 0 || filter < use, $"{member.Trim()} reads _lastRemaining before filtering");
            Assert.True(remaining < 0 || filter < remaining, $"{member.Trim()} reads the timer before filtering");
        }
    }

    [Fact]
    public void LoginWork_RunsFromProfileActivation_NotFromTheLoginEvent()
    {
        // 06-A2: OnLogin fires while the previous character's profile is still active.
        var plugin = ProfileSettingsTests.PluginSource("Plugin.cs");
        var onLogin = ProfileSettingsTests.MethodBody(plugin, "    private void OnLogin()");
        Assert.DoesNotContain("OnLoggedIn()", onLogin);
        Assert.DoesNotContain("_pendingLoginServices", onLogin);
        var activated = ProfileSettingsTests.MethodBody(plugin, "    private void OnProfileActivated()");
        Assert.Contains("_timer.OnLoggedIn();", activated);
        Assert.Contains("_pendingLoginServices = true;", activated);
        Assert.Contains("_relay.ProfileActivated += OnProfileActivated;", plugin);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(plugin, @"_timer\.OnLoggedIn\(\)"));

        var client = ProfileSettingsTests.PluginSource(Path.Combine("Services", "RelayClient.cs"));
        Assert.Contains("        if (_profileActivation.TryRaise(characterReady))\n            RaiseProfileActivated();", client);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(client, @"RaiseProfileActivated\(\);"));
    }

    [Fact]
    public void LoginWork_TheProfileActivationIsSubscribedBeforeAnyTick()
    {
        // 06-A13: the constructor runs off the framework thread; a tick between the two subscriptions could raise the
        // once-per-login event with nobody listening, and the latch would keep it from being raised again.
        var plugin = ProfileSettingsTests.PluginSource("Plugin.cs");
        var ctor = ProfileSettingsTests.MethodBody(plugin, "    public Plugin()");
        var activation = ctor.IndexOf("_relay.ProfileActivated += OnProfileActivated;", StringComparison.Ordinal);
        var update = ctor.IndexOf("Framework.Update += OnFrameworkUpdate;", StringComparison.Ordinal);
        Assert.True(activation >= 0 && update >= 0, "a subscription is missing from the constructor");
        Assert.True(activation < update, "ProfileActivated is subscribed after Framework.Update");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(plugin, @"Framework\.Update \+= "));
    }

    // ---------- 06-A12: one profile activation per login ----------

    [Fact]
    public void ProfileActivation_IsRaisedOncePerLogin()
    {
        var latch = new ProfileActivationLatch();

        Assert.False(latch.TryRaise(profileActive: false));
        Assert.True(latch.TryRaise(profileActive: true));
        for (var i = 0; i < 3; i++)
            Assert.False(latch.TryRaise(profileActive: true));
        Assert.True(latch.Raised);
    }

    [Fact]
    public void ProfileActivation_AClientStateReadError_DoesNotRaiseItAgain()
    {
        // A read error clears the character-ready state for a tick, but isn't a logout: the latch is left alone.
        var latch = new ProfileActivationLatch();
        Assert.True(latch.TryRaise(true));

        Assert.False(latch.TryRaise(false));
        Assert.False(latch.TryRaise(true));
    }

    [Fact]
    public void ProfileActivation_ALogout_ArmsItForTheNextLogin()
    {
        var latch = new ProfileActivationLatch();
        Assert.True(latch.TryRaise(true));

        latch.LoggedOut();

        Assert.False(latch.Raised);
        Assert.False(latch.TryRaise(false));
        Assert.True(latch.TryRaise(true));
        Assert.False(latch.TryRaise(true));
    }

    [Fact]
    public void ProfileActivation_IsReArmedOnlyByALogoutRead_NeverByAReadError()
    {
        var client = ProfileSettingsTests.PluginSource(Path.Combine("Services", "RelayClient.cs"));
        var tick = ProfileSettingsTests.MethodBody(client, "    public void Tick()");
        var catchAt = tick.IndexOf("        catch (Exception ex)\n        {\n            _log.Warning(ex, \"WindUpKey Tick: failed reading client state\");", StringComparison.Ordinal);
        Assert.True(catchAt > 0, "the client-state read's catch not found");
        var read = tick[..catchAt];
        Assert.Contains("            _loggedIn = _clientState.IsLoggedIn;\n            // 06-A12", read);
        Assert.Contains("            if (!_loggedIn)\n                _profileActivation.LoggedOut();", read);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(client, @"_profileActivation\.LoggedOut\(\)"));
    }

    // ---------- 06-A2 / 06-A12: the login sit ----------

    [Fact]
    public void LoginSit_WhenAlreadyPosed_IsSkipped()
    {
        Assert.Equal(LoginSitStep.Done, LoginSitRule.Next(locked: true, canEmote: true, alreadyPosed: true, attempts: 1));
        Assert.Equal(LoginSitStep.Sit, LoginSitRule.Next(locked: true, canEmote: true, alreadyPosed: false, attempts: 1));
    }

    [Fact]
    public void LoginSit_WhenNotLocked_IsDropped()
    {
        Assert.Equal(LoginSitStep.Done, LoginSitRule.Next(locked: false, canEmote: true, alreadyPosed: false, attempts: 0));
    }

    [Fact]
    public void LoginSit_WaitsForTheGame_UpToItsAttempts()
    {
        Assert.Equal(LoginSitStep.Wait, LoginSitRule.Next(true, canEmote: false, alreadyPosed: false, attempts: 1));
        Assert.Equal(LoginSitStep.Wait, LoginSitRule.Next(true, false, false, LoginSitRule.MaxAttempts - 1));
        Assert.Equal(LoginSitStep.Done, LoginSitRule.Next(true, false, false, LoginSitRule.MaxAttempts));

        Assert.True(LoginSitRule.FinishedAfterSit(played: true, attempts: 1));
        Assert.False(LoginSitRule.FinishedAfterSit(played: false, attempts: LoginSitRule.MaxAttempts - 1));
        Assert.True(LoginSitRule.FinishedAfterSit(played: false, attempts: LoginSitRule.MaxAttempts));
    }

    [Fact]
    public void LoginSit_TheTimerUsesTheRule_WithThePoseCheck()
    {
        var timer = ProfileSettingsTests.PluginSource(Path.Combine("Services", "WindTimerService.cs"));
        var sit = ProfileSettingsTests.MethodBody(timer, "    private void TryPendingLoginSit()");
        Assert.Contains("var alreadyPosed = canEmote && _lock.IsInPoseOrEmoteLoop();", sit);
        Assert.Contains("switch (LoginSitRule.Next(locked, canEmote, alreadyPosed, _loginSitAttempts))", sit);
        Assert.Contains("if (LoginSitRule.FinishedAfterSit(_commands.TryExecuteLockEmote(), _loginSitAttempts))", sit);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(sit, @"TryExecuteLockEmote\(\)"));
    }

    // ---------- 06-A7: confirmations tied to the character ----------

    private static bool Paired(string key) => key == Partner;

    [Fact]
    public void OwnerConfirm_ForTheSameDollAndAPairedPartner_MayGrant()
    {
        var confirm = CharacterBoundConfirm.Open(Main, Partner)!;

        Assert.True(confirm.StillValid(Main, isDoll: true, Paired));
    }

    [Fact]
    public void OwnerConfirm_IsRefusedAfterACharacterSwitch()
    {
        var confirm = CharacterBoundConfirm.Open(Main, Partner)!;

        // The alt is paired with the same partner too: still refused.
        Assert.False(confirm.StillValid(Alt, isDoll: true, Paired));
        Assert.False(confirm.StillValid(string.Empty, isDoll: true, Paired));
    }

    [Fact]
    public void OwnerConfirm_IsRefusedAfterAnUnpair()
    {
        var confirm = CharacterBoundConfirm.Open(Main, Partner)!;

        Assert.False(confirm.StillValid(Main, isDoll: true, _ => false));
    }

    [Fact]
    public void OwnerConfirm_IsRefusedOnceNoLongerADoll()
    {
        var confirm = CharacterBoundConfirm.Open(Main, Partner)!;

        Assert.False(confirm.StillValid(Main, isDoll: false, Paired));
    }

    [Fact]
    public void Confirm_CanNotBeOpenedWithoutAnActiveCharacter()
    {
        Assert.Null(CharacterBoundConfirm.Open(string.Empty, Partner));
        Assert.Null(CharacterBoundConfirm.Open(null));
    }

    [Fact]
    public void HardcoreConfirm_IsTiedToTheCharacter_AndNamesNoPartner()
    {
        var confirm = CharacterBoundConfirm.Open(Main)!;
        var asked = false;

        Assert.True(confirm.StillValid(Main, isDoll: true, _ => asked = true));
        Assert.False(asked);
        Assert.False(confirm.StillValid(Alt, isDoll: true, _ => true));
    }

    [Fact]
    public void OwnerCheckbox_OnlyOpensTheConfirm_WhichGrantsAfterItsCheck()
    {
        var window = ProfileSettingsTests.PluginSource(Path.Combine("Ui", "ConfigWindow.cs"));
        var perms = ProfileSettingsTests.MethodBody(window, "    private void DrawPairPermissionsWindow(");
        Assert.DoesNotContain("GrantOwnerAsync", perms);
        Assert.Contains("_ownerConfirm = CharacterBoundConfirm.Open(_config.ActiveContentId, actionPartnerKey);", perms);

        var confirm = ProfileSettingsTests.MethodBody(window, "    private void DrawOwnerGrantConfirm()");
        var check = confirm.IndexOf("pending.StillValid(_config.ActiveContentId, _config.IsDoll,", StringComparison.Ordinal);
        var grant = confirm.IndexOf("_relay.GrantOwnerAsync(partnerKey)", StringComparison.Ordinal);
        Assert.True(check >= 0 && grant > check, "the grant isn't behind the confirm's check");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(window, "GrantOwnerAsync"));
        Assert.Contains("_hardcoreConfirm.StillValid(_config.ActiveContentId, _config.IsDoll, _ => true)", window);
    }

    [Fact]
    public void HardcoreConfirm_IsDroppedEveryFrame_WhicheverTabIsDrawn()
    {
        // 06-A12: the Safeword tab, which draws the confirm, is hidden once she isn't a doll; the check doesn't depend on it.
        var window = ProfileSettingsTests.PluginSource(Path.Combine("Ui", "ConfigWindow.cs"));
        var draw = ProfileSettingsTests.MethodBody(window, "    public override void Draw()");
        var drop = draw.IndexOf("DropStaleConfirms();", StringComparison.Ordinal);
        var role = draw.IndexOf("if (!_config.HasChosenRole)", StringComparison.Ordinal);
        Assert.True(drop >= 0 && role > drop, "Draw doesn't drop stale confirms before anything else");

        var stale = ProfileSettingsTests.MethodBody(window, "    private void DropStaleConfirms()");
        Assert.Contains("if (_hardcoreConfirm is not null && !_hardcoreConfirm.StillValid(_config.ActiveContentId, _config.IsDoll, _ => true))\n            _hardcoreConfirm = null;", stale);
    }

    [Fact]
    public void OwnerConfirmError_IsDismissedOnACharacterChange_ATabChange_OrClosing()
    {
        var window = ProfileSettingsTests.PluginSource(Path.Combine("Ui", "ConfigWindow.cs"));
        var stale = ProfileSettingsTests.MethodBody(window, "    private void DropStaleConfirms()");
        Assert.Contains("&& !string.Equals(_ownerConfirmErrorContentId, _config.ActiveContentId, StringComparison.Ordinal))\n            _ownerConfirmError = null;", stale);

        var confirm = ProfileSettingsTests.MethodBody(window, "    private void DrawOwnerGrantConfirm()");
        Assert.Contains("_ownerConfirmError = \"The character or the pairing changed, so no owner was added.\";\n            _ownerConfirmErrorContentId = _config.ActiveContentId;", confirm);
        // Confirm and Cancel both dismiss it.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(confirm, @"_ownerConfirm = null;\n            _ownerConfirmError = null;").Count);

        var draw = ProfileSettingsTests.MethodBody(window, "    public override void Draw()");
        Assert.Contains("            DrawPairingTab();\n            pairingDrawn = true;", draw);
        Assert.Contains("        if (!pairingDrawn)\n            _ownerConfirmError = null;", draw);
        Assert.Contains("_ownerConfirmError = null;", ProfileSettingsTests.MethodBody(window, "    public override void OnClose()"));
    }

    // ---------- 06-A9: a queued message from a superseded connection ----------

    [Fact]
    public void Inbound_OnItsOwnConnectionAndCharacter_IsHandled()
    {
        var policy = new RelayConnectPolicy();
        var epoch = policy.Epoch;

        Assert.True(RelayConnectPolicy.InboundStillCurrent(epoch, Main, policy.Epoch, Main));
    }

    [Fact]
    public void Inbound_QueuedBeforeACharacterSwitch_IsDropped()
    {
        // The connection registered for the main; a message is read and queued for the framework thread. Before it runs,
        // the tick activates the alt, which resets the connect cycle (ClearAuthBlock) and closes the socket. Closing the
        // socket doesn't cancel what is already queued, so only this check keeps it from applying to the alt.
        var policy = new RelayConnectPolicy();
        var arrivedEpoch = policy.Epoch;

        policy.Reset();

        Assert.False(RelayConnectPolicy.InboundStillCurrent(arrivedEpoch, Main, policy.Epoch, Alt));
    }

    [Fact]
    public void Inbound_IsDroppedWhenEitherTheEpochOrTheCharacterChanged()
    {
        Assert.False(RelayConnectPolicy.InboundStillCurrent(3, Main, 4, Main));
        Assert.False(RelayConnectPolicy.InboundStillCurrent(3, Main, 3, Alt));
        Assert.False(RelayConnectPolicy.InboundStillCurrent(3, Main, 3, string.Empty));
    }

    [Fact]
    public void Inbound_FromAConnectionWithoutAProfile_IsNeverHandled()
    {
        Assert.False(RelayConnectPolicy.InboundStillCurrent(3, string.Empty, 3, string.Empty));
        Assert.False(RelayConnectPolicy.InboundStillCurrent(3, null, 3, null));
    }

    [Fact]
    public void Inbound_TheReceiveLoopChecksEveryQueuedMessage()
    {
        // RelayClient needs Dalamud: check that the queued handler goes through the rule with this connection's values.
        var client = ProfileSettingsTests.PluginSource(Path.Combine("Services", "RelayClient.cs"));
        var loop = ProfileSettingsTests.MethodBody(client, "private async Task<string?> ReceiveLoopAsync(");
        Assert.Contains(
            "RelayConnectPolicy.InboundStillCurrent(epoch, profile, _connectPolicy.Epoch, _config.ActiveContentId)\n"
            + "                        ? HandleMessageAsync(envelope, ct)\n"
            + "                        : DropStaleInbound(),",
            loop);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(client, @"HandleMessageAsync\(envelope, ct\)"));
        Assert.Contains("profile = _config.ActiveContentId;", client);
        Assert.Contains("ReceiveLoopAsync(connection.Socket, epoch, connection.Profile, ct)", client);
    }
}
