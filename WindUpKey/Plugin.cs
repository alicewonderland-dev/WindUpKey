using System;
using System.Collections.Generic;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System.Diagnostics;
using WindUpKey.Services;
#if WINDUP_TESTING
using WindUpKey.SelfTest;
#endif
using WindUpKey.Sources;
using WindUpKey.Ui;

namespace WindUpKey;

/// <summary>Composition root only — wire services here, put logic in Services/Sources/Ui.</summary>
public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/windup";
    private const string HardcoreUnlockConfirmation =
        "I am a very dumb doll who got stuck in hardcore";
    private static readonly TimeSpan HardcoreReUnlockCooldown = TimeSpan.FromHours(72);

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IContextMenu ContextMenu { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    /// <summary>07-B3: a Call is cross-checked against the owner's party-list territory.</summary>
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IUnlockState UnlockState { get; private set; } = null!;
    [PluginService] internal static IDutyState DutyState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static ITextureProvider Textures { get; private set; } = null!;
    [PluginService] internal static IDtrBar DtrBar { get; private set; } = null!;
#if WINDUP_TESTING
    /// <summary>04-D8: the self-test resolves hook signatures with it, without installing a hook.</summary>
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
#endif

    public Configuration Configuration { get; }
    private readonly WindowSystem _windowSystem = new("WindUpKey");
    private readonly ConfigWindow _configWindow;
    private readonly MainWindow _mainWindow;
    private readonly ServerInfoBarService _serverInfoBar;
    private readonly ChangelogWindow _changelogWindow;
    private readonly InputDiagnosticLog _inputDiagnostics;
    private readonly LockController _lockController;
    private readonly WindTimerService _timer;
    private readonly LowWindWarningService _lowWind;
    private readonly ConsentService _consent;
    private readonly IWindNotifier _notifier;
    private readonly SoundEffectService _sounds;
    private readonly RelayClient _relay;
    private readonly MoodlesWindStatusService _moodlesStatus;
    private readonly CallPromptWindow _callPrompt;
    private readonly UnwindConfirmWindow _unwindConfirm;
    private readonly DiscordPromptWindow _discordPrompt;
    private readonly CallTravelService _callTravel;
#if WINDUP_TESTING
    private readonly SelfTestController _selfTest;
#endif
    private readonly List<IWindUpSource> _sources = [];
    private long _lastFrameworkUpdateTimestamp;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        try
        {
            Configuration.Migrate();
            Configuration.Save();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "WindUpKey config migrate/save failed; continuing with defaults where possible");
        }

        var commands = new GameCommandRunner(Log, DataManager, UnlockState, Configuration);
        _inputDiagnostics = new InputDiagnosticLog(
            PluginInterface,
            Log,
            PluginInterface.Manifest.AssemblyVersion.ToString(),
#if WINDUP_TESTING
            enabled: true
#else
            enabled: false
#endif
        );
        _lockController = new LockController(
            GameInterop,
            ClientState,
            Condition,
            ObjectTable,
            commands,
            Configuration,
            Log,
            _inputDiagnostics);
        var lowWindMessages = new LowWindMessagesConfig(PluginInterface.GetPluginConfigDirectory(), Log);
        var soundsDir = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(PluginInterface.AssemblyLocation.FullName) ?? ".",
            "Sounds");
        _sounds = new SoundEffectService(Configuration, Log, soundsDir);
        _lowWind = new LowWindWarningService(Configuration, ChatGui, lowWindMessages, _sounds);
        _timer = new WindTimerService(Configuration, _lockController, commands, ObjectTable, Condition, _lowWind, ChatGui);
        _consent = new ConsentService(Configuration);
        _notifier = new ChatWindNotifier(ChatGui);
        var quests = new QuestBoardService(
            Configuration, _timer, DutyState, DataManager, ClientState, Condition, Framework, PlayerState, PartyList, ChatGui, Log);
        _relay = new RelayClient(
            Configuration, ClientState, ObjectTable, Condition, Framework, Log, ChatGui, DataManager, PluginInterface, _consent, _timer, _notifier, _sounds, commands);
        _moodlesStatus = new MoodlesWindStatusService(
            PluginInterface, ClientState, ObjectTable, Configuration, _timer, lowWindMessages, Log);

        _callPrompt = new CallPromptWindow();
        _callTravel = new CallTravelService(
            PluginInterface,
            ClientState,
            ObjectTable,
            PartyList,
            TargetManager,
            GameGui,
            Condition,
            DataManager,
            ChatGui,
            Log,
            Configuration,
            _timer,
            _callPrompt,
            ack => _relay.SendCallAckAsync(ack),
            result => _relay.SendCallResultAsync(result),
            _consent.CanReceiveCallFromKey);
        _relay.AttachCallTravel(_callTravel);

#if WINDUP_TESTING
        // 04: /windup selftest (Testing builds only, debug mode). The run's own Reconnect sets the epoch it expects.
        SelfTestController? selfTest = null;
        _selfTest = selfTest = new SelfTestController(
            new SelfTestHost(
                _relay,
                Configuration,
                Framework,
                ObjectTable,
                DataManager,
                SigScanner,
                PluginInterface,
                _inputDiagnostics,
                commands,
                () => _callTravel.IsTravelReady,
                () => new CallIdleFact(
                    _callTravel.HasActiveCall,
                    _callTravel.IsLandingGraceActive,
                    _timer.IsCallTravelBypassActive,
                    _lockController.IsInputMuted),
                epoch => selfTest?.ExpectEpoch(epoch),
                () => selfTest?.ReconnectAllowed == true),
            _relay,
            Framework,
            ChatGui,
            Log,
            PluginInterface.GetPluginConfigDirectory(),
            PluginInterface.Manifest.AssemblyVersion.ToString(),
            RelayDefaults.RelayToken);
#endif

        _changelogWindow = new ChangelogWindow();
        var version = PluginInterface.Manifest.AssemblyVersion.ToString();
        _configWindow = new ConfigWindow(
            Configuration,
            _relay,
            _timer,
            PluginInterface,
            TargetManager,
            commands,
            _moodlesStatus,
            lowWindMessages.FilePath,
            _changelogWindow,
            Textures,
            version);
        _windowSystem.AddWindow(_configWindow);
        // The main window and the pop-outs it opens (GagSpeak-style), or holds as tabs; Settings is the window above.
        var windWindow = new WindWindow(Configuration, _relay, _timer, _configWindow);
        var pairsWindow = new PairsWindow(Configuration, _configWindow);
        var questsWindow = new QuestsWindow(Configuration, quests, Textures);
        var safewordWindow = new SafewordWindow(Configuration, _configWindow, UseSafeword);
        _mainWindow = new MainWindow(
            Configuration, _relay, _timer, _moodlesStatus, Textures, _configWindow,
            windWindow, pairsWindow, questsWindow, safewordWindow, version);
        _windowSystem.AddWindow(_mainWindow);
        _serverInfoBar = new ServerInfoBarService(DtrBar, Configuration, _relay, ToggleMain);
        _windowSystem.AddWindow(windWindow);
        _windowSystem.AddWindow(pairsWindow);
        _windowSystem.AddWindow(questsWindow);
        _windowSystem.AddWindow(safewordWindow);
        _windowSystem.AddWindow(_changelogWindow);
        _windowSystem.AddWindow(_callPrompt);
        _unwindConfirm = new UnwindConfirmWindow(_relay, Configuration);
        _windowSystem.AddWindow(_unwindConfirm);
        _discordPrompt = new DiscordPromptWindow(Configuration, Textures);
        _windowSystem.AddWindow(_discordPrompt);
        quests.GrantSprings = (lines, requestId) => _relay.Forget(_relay.SendSpringGrantAsync(lines, requestId), "quest springs");
        _relay.SpringGrantSettled = quests.OnSpringGrantSettled;
        quests.CanGrantSprings = () => _relay.IsConnected && _relay.SpringBalance is not null;

        var contextMenuSource = new ContextMenuWindSource(ContextMenu, ClientState, Configuration, _relay, Log);
        _sources.Add(contextMenuSource);
        var commendationSource = new CommendationWindSource(
            DutyState, PlayerState, Framework, ClientState, Configuration, _timer, Log);
        _sources.Add(commendationSource);
        _sources.Add(quests);
        foreach (var source in _sources)
            source.Enable();

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage =
                "Open Wind-Up Key.\n/windup safeword <word> uses your safeword (and pauses remote control).\n/windup safeword resume ends that pause early.\n/windup unlock clears Hardcore and all owners (confirmation required)."
                + "\n/windup stopcall declines an active owner call (not in Hardcore)."
#if WINDUP_TESTING
                + "\n/windup selftest runs the relay self-test (debug mode)."
#endif
                ,
        });

        PluginInterface.UiBuilder.Draw += DrawUi;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfig;
        PluginInterface.UiBuilder.OpenMainUi += OpenMain;

        // 06-A2: login work runs once the logged-in character's profile is active (also after a load while logged in).
        // 06-A13: subscribed before Framework.Update: this constructor runs off the framework thread, and a tick in
        // between could raise the one-per-login event with nobody listening (losing the login sit, reminder and Moodles).
        _relay.ProfileActivated += OnProfileActivated;
        Framework.Update += OnFrameworkUpdate;
        ClientState.Login += OnLogin;
        ClientState.Logout += OnLogout;

        // Always start the loop; it waits until character data is ready.
        // Do not gate on IsLoggedIn — Login may already have fired before load.
        _relay.Start();

        if (!Configuration.HasChosenRole)
            OpenMain();

        Log.Information("WindUpKey loaded.");
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        ClientState.Login -= OnLogin;
        ClientState.Logout -= OnLogout;
        _relay.ProfileActivated -= OnProfileActivated;
        PluginInterface.UiBuilder.Draw -= DrawUi;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfig;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMain;
        CommandManager.RemoveHandler(CommandName);

        foreach (var source in _sources)
            source.Dispose();
        _sources.Clear();

#if WINDUP_TESTING
        // Before the relay: cancels a run and lifts its outage hold (04-D2).
        _selfTest.Dispose();
#endif
        _relay.Dispose();
        _callTravel.Dispose();
        _moodlesStatus.Dispose();
        _sounds.Dispose();
        _lockController.Dispose();
        _inputDiagnostics.Dispose();
        _serverInfoBar.Dispose();
        _windowSystem.RemoveAllWindows();
        _configWindow.Dispose();
    }

    /// <summary>
    /// With tabs, a pop-out window asked to open since the last frame opens as the main window's tab instead; then every
    /// window draws.
    /// </summary>
    private void DrawUi()
    {
        _mainWindow.RouteToTabs();
        _windowSystem.Draw();
    }

    private void OpenConfig() => _configWindow.IsOpen = true;

    private void OpenMain() => _mainWindow.IsOpen = true;

    /// <summary>The server info bar's click: opens the main window (in front, expanded), or closes it.</summary>
    private void ToggleMain() => _mainWindow.ShowOrHide();

    private bool _pendingLoginServices;
    private bool _wasBetweenAreas;

    private void OnFrameworkUpdate(IFramework framework)
    {
        var updateStart = Stopwatch.GetTimestamp();
        var frameGapMilliseconds = _lastFrameworkUpdateTimestamp == 0
            ? 0
            : Stopwatch.GetElapsedTime(_lastFrameworkUpdateTimestamp, updateStart).TotalMilliseconds;
        _lastFrameworkUpdateTimestamp = updateStart;
        var slowestPhase = "none";
        var slowestPhaseMilliseconds = 0d;

        var loading = Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas]
                      || Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas51];

        var phaseStart = Stopwatch.GetTimestamp();
        _relay.Tick();
        RecordSlowestPhase("relay", phaseStart, ref slowestPhase, ref slowestPhaseMilliseconds);

        phaseStart = Stopwatch.GetTimestamp();
        _timer.SetRelaySafetyBypass(_relay.ShouldSuspendMovementLocks);
        _timer.Tick();
        RecordSlowestPhase("timer", phaseStart, ref slowestPhase, ref slowestPhaseMilliseconds);
        phaseStart = Stopwatch.GetTimestamp();
        _relay.TickCallTravel();
        _unwindConfirm.Sync();
        _serverInfoBar.Update();
        // After the role is chosen, so it never sits on top of the first-time setup.
        _discordPrompt.Sync(ClientState.IsLoggedIn && Configuration.HasChosenRole);
        RecordSlowestPhase("call-travel", phaseStart, ref slowestPhase, ref slowestPhaseMilliseconds);
#if WINDUP_TESTING
        _selfTest.Tick();
#endif
        phaseStart = Stopwatch.GetTimestamp();
        _lowWind.Tick();
        RecordSlowestPhase("low-wind", phaseStart, ref slowestPhase, ref slowestPhaseMilliseconds);

        // Moodles reflection during zone load can crash the client; wait until stable.
        if (!loading)
        {
            phaseStart = Stopwatch.GetTimestamp();
            if (_pendingLoginServices)
            {
                _pendingLoginServices = false;
                _lowWind.OnLoggedIn();
                // Moodles may have been cleared on logout; re-apply once world is stable.
                _moodlesStatus.OnLoggedIn();
            }

            if (_wasBetweenAreas)
                _moodlesStatus.OnAreaTransition();

            _moodlesStatus.Tick();
            RecordSlowestPhase("moodles", phaseStart, ref slowestPhase, ref slowestPhaseMilliseconds);
        }

        _wasBetweenAreas = loading;
        phaseStart = Stopwatch.GetTimestamp();
        _lockController.Tick();
        RecordSlowestPhase("lock-controller", phaseStart, ref slowestPhase, ref slowestPhaseMilliseconds);

        phaseStart = Stopwatch.GetTimestamp();
        _inputDiagnostics.Tick();
        RecordSlowestPhase("diagnostic-flush", phaseStart, ref slowestPhase, ref slowestPhaseMilliseconds);

        var pluginMilliseconds = Stopwatch.GetElapsedTime(updateStart).TotalMilliseconds;
        _inputDiagnostics.RecordFrameTiming(
            frameGapMilliseconds,
            pluginMilliseconds,
            slowestPhase,
            slowestPhaseMilliseconds);
    }

    private static void RecordSlowestPhase(
        string name,
        long phaseStart,
        ref string slowestPhase,
        ref double slowestPhaseMilliseconds)
    {
        var elapsed = Stopwatch.GetElapsedTime(phaseStart).TotalMilliseconds;
        if (elapsed <= slowestPhaseMilliseconds)
            return;

        slowestPhase = name;
        slowestPhaseMilliseconds = elapsed;
    }

    private void OnLogin()
    {
        // 06-A2: the login sit and reminders wait for ProfileActivated: here the previous character's profile is
        // still the active one (activation waits for the local player).
        _relay.Start();
    }

    /// <summary>
    /// 06-A2: from <see cref="RelayClient.Tick"/> (framework thread), once per login or plugin load, after the logged-in
    /// character's profile became active.
    /// </summary>
    private void OnProfileActivated()
    {
        _timer.OnLoggedIn();
        // LowWind / Moodles OnLoggedIn deferred until !BetweenAreas (see OnFrameworkUpdate).
        _pendingLoginServices = true;
    }

    private void OnLogout(int type, int code)
    {
        _relay.CancelActiveCall();
#if WINDUP_TESTING
        // Its restore then only lifts the hold, never reconnecting a logged-out client (04-D10).
        _selfTest.OnLogout();
#endif
        _lockController.UninstallHooks();
        _relay.Stop();
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            OpenMain();
            return;
        }

        var space = trimmed.IndexOf(' ');
        var verb = space < 0 ? trimmed : trimmed[..space];
        var rest = space < 0 ? string.Empty : trimmed[(space + 1)..].Trim();

#if WINDUP_TESTING
        if (string.Equals(verb, "selftest", StringComparison.OrdinalIgnoreCase))
        {
            // Like check and debug: debug mode only (04-D1).
            if (!Configuration.IsDebugEnabled)
            {
                PluginChat.PrintError(ChatGui, "Enable debug mode in config to use /windup selftest.");
                return;
            }

            _selfTest.OnCommand(rest);
            return;
        }
#endif

        if (string.Equals(verb, "stopcall", StringComparison.OrdinalIgnoreCase))
        {
            // 07-B1 (D5): Hardcore has no way out of a Call (its deadlines, stuck detection, consent loss and logout
            // still end it).
            if (!CallAskFirst.MayDecline(Configuration.HardcoreMode))
            {
                PluginChat.PrintError(ChatGui, CallAskFirst.HardcoreStopRefusal);
                return;
            }

            // 07-B1: like the Stop button, it counts as a Decline (the next tick runs it, and says so in chat).
            if (!_relay.RequestDeclineActiveCall())
                PluginChat.Print(ChatGui, "There's no Call to stop.", PluginChat.Grey);
            return;
        }

        if (string.Equals(verb, "unlock", StringComparison.OrdinalIgnoreCase))
        {
            var hasOwners = Configuration.HasOwners;
            var hardcoreOn = Configuration.HardcoreMode;

            if (!hardcoreOn && !hasOwners)
            {
                PluginChat.Print(ChatGui, "Nothing to unlock (Hardcore is off and you have no owners).", PluginChat.Grey);
                return;
            }

            if (!string.Equals(rest, HardcoreUnlockConfirmation, StringComparison.Ordinal))
            {
                PluginChat.Print(
                    ChatGui,
                    "To clear Hardcore and/or all owners, use: /windup unlock I am a very dumb doll who got stuck in hardcore",
                    PluginChat.White);
                return;
            }

            // Confirmed clear attempt — 72h lockout blocks Hardcore clear without the success echo.
            if (hardcoreOn
                && Configuration.HardcoreLastClearedUtc is { } lastCleared
                && DateTimeOffset.UtcNow - lastCleared < HardcoreReUnlockCooldown)
            {
                PluginChat.Print(
                    ChatGui,
                    "You are a very, very dumb doll who got stuck in hardcore... multiple times! Hardcore is locked for 3 days.",
                    PluginChat.Purple);
                // Still allow clearing owners during Hardcore cooldown.
                if (!hasOwners)
                    return;
            }
            else if (hardcoreOn)
            {
                Configuration.HardcoreMode = false;
                Configuration.HardcoreLastClearedUtc = DateTimeOffset.UtcNow;
                Configuration.Save();
                PluginChat.Print(ChatGui, "Hardcore cleared. You can change role again.", PluginChat.Green);
            }

            if (hasOwners)
            {
                _relay.Forget(_relay.ClearAllOwnersAsync(), "clear owners");
                PluginChat.Print(ChatGui, "All owners cleared. Owner-locked settings are unlocked.", PluginChat.Green);
            }

            return;
        }

        if (string.Equals(verb, "check", StringComparison.OrdinalIgnoreCase))
        {
            if (!Configuration.IsDebugEnabled)
            {
                PluginChat.PrintError(ChatGui, "Enable debug mode in config to use /windup check.");
                return;
            }

            _lowWind.PrintCheckStatus();
            return;
        }

        if (string.Equals(verb, "debug", StringComparison.OrdinalIgnoreCase))
        {
            if (!Configuration.IsDebugEnabled)
            {
                PluginChat.PrintError(ChatGui, "Enable debug mode in config to use /windup debug.");
                return;
            }

            _lowWind.PrintDebugStatus();
            return;
        }

        if (string.Equals(verb, "safeword", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(rest))
            {
                PluginChat.PrintError(ChatGui, "Usage: /windup safeword <word>, or /windup safeword resume");
                return;
            }

            // 06-B1: "resume" ends the pause early (whatever the role or Hardcore), unless it is her usable safeword.
            var safewordUsable = Configuration.IsDoll && Configuration.SafewordEnabled && !Configuration.HardcoreMode;
            if (SafewordPause.IsResume(rest, safewordUsable, Configuration.Safeword))
            {
                if (_timer.ResumeRemoteControl())
                    PluginChat.Print(ChatGui, "Remote control resumed.", PluginChat.Green);
                else
                    PluginChat.Print(ChatGui, "Remote control isn't paused.", PluginChat.Grey);
                return;
            }

            UseSafeword(rest);
            return;
        }

        PluginChat.PrintError(ChatGui, "Unknown command. Try /windup, /windup safeword <word>, or /windup unlock.");
    }

    /// <summary>
    /// The safeword typed by the doll, from <c>/windup safeword &lt;word&gt;</c> or the Safeword window. The result is
    /// printed to chat either way.
    /// </summary>
    private void UseSafeword(string word)
    {
        if (!Configuration.IsDoll)
        {
            PluginChat.PrintError(ChatGui, "Safeword is only available in Doll mode.");
            return;
        }

        if (!Configuration.SafewordEnabled)
        {
            PluginChat.PrintError(ChatGui, "Safeword is disabled in Settings.");
            return;
        }

        if (Configuration.HardcoreMode)
        {
            PluginChat.PrintError(ChatGui, "Safeword is disabled while Hardcore is on.");
            return;
        }

        // 06-A4: compared trimmed, as it is stored.
        if (!SafewordText.Matches(word, Configuration.Safeword))
        {
            PluginChat.PrintError(ChatGui, "Incorrect safeword.");
            return;
        }

        // 06-B1: pauses remote control, adds the safeword hours ignoring the owner's cap (never shortening the timer),
        // and stops an owner's Call. The safeword itself is never logged.
        // 06-B3: wind first, so cancelling the Call never briefly sees her unwound (re-lock, sit, then stand up).
        _timer.UseSafeword(Configuration.SafewordHours);
        _relay.CancelActiveCall();
        // Confirmation only — never print remaining time to the doll.
        PluginChat.Print(ChatGui, "Safeword used. Remote control is paused for now.", PluginChat.Green);
    }
}
