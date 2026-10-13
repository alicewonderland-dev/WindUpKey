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
#if WINDUP_TESTING
    /// <summary>07-B3: a Call is cross-checked against the owner's party-list territory.</summary>
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
#endif
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IUnlockState UnlockState { get; private set; } = null!;
    [PluginService] internal static IDutyState DutyState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
#if WINDUP_TESTING
    /// <summary>04-D8: the self-test resolves hook signatures with it, without installing a hook.</summary>
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
#endif

    public Configuration Configuration { get; }
    private readonly WindowSystem _windowSystem = new("WindUpKey");
    private readonly ConfigWindow _configWindow;
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
#if WINDUP_TESTING
    private readonly CallPromptWindow _callPrompt;
    private readonly CallTravelService _callTravel;
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
#if WINDUP_TESTING
        var quests = new QuestWindService(
            Configuration, _timer, DutyState, DataManager, ClientState, ChatGui, Log);
#endif
        _relay = new RelayClient(
            Configuration, ClientState, ObjectTable, Condition, Framework, Log, ChatGui, DataManager, PluginInterface, _consent, _timer, _notifier, _sounds, commands);
        _moodlesStatus = new MoodlesWindStatusService(
            PluginInterface, ClientState, ObjectTable, Configuration, _timer, lowWindMessages, Log);

#if WINDUP_TESTING
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
        _configWindow = new ConfigWindow(
            Configuration,
            _relay,
            _timer,
#if WINDUP_TESTING
            quests,
#endif
            TargetManager,
            commands,
            _moodlesStatus,
            lowWindMessages.FilePath,
            _changelogWindow,
            PluginInterface.Manifest.AssemblyVersion.ToString());
        _windowSystem.AddWindow(_configWindow);
        _windowSystem.AddWindow(_changelogWindow);
#if WINDUP_TESTING
        _windowSystem.AddWindow(_callPrompt);
#endif

        var contextMenuSource = new ContextMenuWindSource(ContextMenu, ClientState, Configuration, _relay, Log);
        _sources.Add(contextMenuSource);
        var commendationSource = new CommendationWindSource(
            DutyState, PlayerState, Framework, ClientState, Configuration, _timer, Log);
        _sources.Add(commendationSource);
#if WINDUP_TESTING
        _sources.Add(quests);
#endif
        foreach (var source in _sources)
            source.Enable();

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage =
                "Open Wind-Up Key config.\n/windup safeword <word> uses your safeword (and pauses remote control).\n/windup safeword resume ends that pause early.\n/windup unlock clears Hardcore and all owners (confirmation required)."
#if WINDUP_TESTING
                + "\n/windup stopcall declines an active owner call (not in Hardcore)."
                + "\n/windup selftest runs the relay self-test (debug mode)."
#endif
                ,
        });

        PluginInterface.UiBuilder.Draw += _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfig;
        PluginInterface.UiBuilder.OpenMainUi += OpenConfig;

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
            OpenConfig();

        Log.Information("WindUpKey loaded.");
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        ClientState.Login -= OnLogin;
        ClientState.Logout -= OnLogout;
        _relay.ProfileActivated -= OnProfileActivated;
        PluginInterface.UiBuilder.Draw -= _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfig;
        PluginInterface.UiBuilder.OpenMainUi -= OpenConfig;
        CommandManager.RemoveHandler(CommandName);

        foreach (var source in _sources)
            source.Dispose();
        _sources.Clear();

#if WINDUP_TESTING
        // Before the relay: cancels a run and lifts its outage hold (04-D2).
        _selfTest.Dispose();
#endif
        _relay.Dispose();
#if WINDUP_TESTING
        _callTravel.Dispose();
#endif
        _moodlesStatus.Dispose();
        _sounds.Dispose();
        _lockController.Dispose();
        _inputDiagnostics.Dispose();
        _windowSystem.RemoveAllWindows();
        _configWindow.Dispose();
    }

    private void OpenConfig() => _configWindow.IsOpen = true;

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
#if WINDUP_TESTING
        phaseStart = Stopwatch.GetTimestamp();
        _relay.TickCallTravel();
        RecordSlowestPhase("call-travel", phaseStart, ref slowestPhase, ref slowestPhaseMilliseconds);
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
#if WINDUP_TESTING
        _relay.CancelActiveCall();
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
            OpenConfig();
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
#endif

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
                _ = _relay.ClearAllOwnersAsync();
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

            if (!Configuration.IsDoll)
            {
                PluginChat.PrintError(ChatGui, "Safeword is only available in Doll mode.");
                return;
            }

            if (!Configuration.SafewordEnabled)
            {
                PluginChat.PrintError(ChatGui, "Safeword is disabled in config.");
                return;
            }

            if (Configuration.HardcoreMode)
            {
                PluginChat.PrintError(ChatGui, "Safeword is disabled while Hardcore is on.");
                return;
            }

            // 06-A4: compared trimmed, as it is stored.
            if (!SafewordText.Matches(rest, Configuration.Safeword))
            {
                PluginChat.PrintError(ChatGui, "Incorrect safeword.");
                return;
            }

            // 06-B1: pauses remote control, adds the safeword hours ignoring the owner's cap (never shortening the timer),
            // and stops an owner's Call. The safeword itself is never logged.
            // 06-B3: wind first, so cancelling the Call never briefly sees her unwound (re-lock, sit, then stand up).
            _timer.UseSafeword(Configuration.SafewordHours);
#if WINDUP_TESTING
            _relay.CancelActiveCall();
#endif
            // Confirmation only — never print remaining time to the doll.
            PluginChat.Print(ChatGui, "Safeword used. Remote control is paused for now.", PluginChat.Green);
            return;
        }

        PluginChat.PrintError(ChatGui, "Unknown command. Try /windup, /windup safeword <word>, or /windup unlock.");
    }
}
