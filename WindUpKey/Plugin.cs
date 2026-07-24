using System;
using System.Collections.Generic;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using WindUpKey.Services;
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
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IUnlockState UnlockState { get; private set; } = null!;
    [PluginService] internal static IDutyState DutyState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;

    public Configuration Configuration { get; }
    private readonly WindowSystem _windowSystem = new("WindUpKey");
    private readonly ConfigWindow _configWindow;
    private readonly ChangelogWindow _changelogWindow;
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
#endif
    private readonly List<IWindUpSource> _sources = [];

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
        _lockController = new LockController(GameInterop, ClientState, Condition, ObjectTable, commands, Configuration, Log);
        var lowWindMessages = new LowWindMessagesConfig(PluginInterface.GetPluginConfigDirectory(), Log);
        var soundsDir = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(PluginInterface.AssemblyLocation.FullName) ?? ".",
            "Sounds");
        _sounds = new SoundEffectService(Configuration, Log, soundsDir);
        _lowWind = new LowWindWarningService(Configuration, ChatGui, lowWindMessages, _sounds);
        _timer = new WindTimerService(Configuration, _lockController, commands, ObjectTable, Condition, _lowWind, ChatGui);
        _consent = new ConsentService(Configuration);
        _notifier = new ChatWindNotifier(ChatGui);
        _relay = new RelayClient(
            Configuration, ClientState, ObjectTable, Framework, Log, ChatGui, _consent, _timer, _notifier, _sounds, commands);
        _moodlesStatus = new MoodlesWindStatusService(
            PluginInterface, ClientState, ObjectTable, Configuration, _timer, lowWindMessages, Log);

#if WINDUP_TESTING
        _callPrompt = new CallPromptWindow();
        _callTravel = new CallTravelService(
            PluginInterface,
            ClientState,
            ObjectTable,
            Condition,
            DataManager,
            ChatGui,
            Log,
            Configuration,
            _timer,
            _callPrompt,
            ack => _relay.SendCallAckAsync(ack),
            result => _relay.SendCallResultAsync(result));
        _relay.AttachCallTravel(_callTravel);
#endif

        _changelogWindow = new ChangelogWindow();
        _configWindow = new ConfigWindow(
            Configuration,
            _relay,
            _timer,
            TargetManager,
            commands,
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
        foreach (var source in _sources)
            source.Enable();

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage =
                "Open Wind-Up Key config.\n/windup safeword <word> uses your safeword.\n/windup unlock clears Hardcore and all owners (confirmation required)."
#if WINDUP_TESTING
                + "\n/windup stopcall cancels an active owner call."
#endif
                ,
        });

        PluginInterface.UiBuilder.Draw += _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfig;
        PluginInterface.UiBuilder.OpenMainUi += OpenConfig;

        Framework.Update += OnFrameworkUpdate;
        ClientState.Login += OnLogin;
        ClientState.Logout += OnLogout;

        // Always start the loop; it waits until character data is ready.
        // Do not gate on IsLoggedIn — Login may already have fired before load.
        _relay.Start();

        if (ClientState.IsLoggedIn)
        {
            _timer.OnLoggedIn();
            _pendingLoginServices = true;
        }

        if (!Configuration.HasChosenRole)
            OpenConfig();

        Log.Information("WindUpKey loaded.");
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        ClientState.Login -= OnLogin;
        ClientState.Logout -= OnLogout;
        PluginInterface.UiBuilder.Draw -= _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfig;
        PluginInterface.UiBuilder.OpenMainUi -= OpenConfig;
        CommandManager.RemoveHandler(CommandName);

        foreach (var source in _sources)
            source.Dispose();
        _sources.Clear();

        _relay.Dispose();
#if WINDUP_TESTING
        _callTravel.Dispose();
#endif
        _moodlesStatus.Dispose();
        _sounds.Dispose();
        _lockController.Dispose();
        _windowSystem.RemoveAllWindows();
        _configWindow.Dispose();
    }

    private void OpenConfig() => _configWindow.IsOpen = true;

    private bool _pendingLoginServices;
    private bool _wasBetweenAreas;

    private void OnFrameworkUpdate(IFramework framework)
    {
        var loading = Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas]
                      || Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas51];

        _relay.Tick();
        _timer.SetRelaySafetyBypass(_relay.ShouldSuspendMovementLocks);
        _timer.Tick();
#if WINDUP_TESTING
        _relay.TickCallTravel();
#endif
        _lowWind.Tick();

        // Moodles reflection during zone load can crash the client; wait until stable.
        if (!loading)
        {
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
        }

        _wasBetweenAreas = loading;
        _lockController.Tick();
    }

    private void OnLogin()
    {
        _pendingLoginServices = true;
        _relay.Start();
        _timer.OnLoggedIn();
        // LowWind / Moodles OnLoggedIn deferred until !BetweenAreas (see OnFrameworkUpdate).
    }

    private void OnLogout(int type, int code)
    {
#if WINDUP_TESTING
        _relay.CancelActiveCall();
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
        if (string.Equals(verb, "stopcall", StringComparison.OrdinalIgnoreCase))
        {
            _relay.CancelActiveCall();
            PluginChat.Print(ChatGui, "Call cancelled.", PluginChat.Grey);
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
                PluginChat.PrintError(ChatGui, "Usage: /windup safeword <word>");
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

            if (!string.Equals(rest, Configuration.Safeword, StringComparison.Ordinal))
            {
                PluginChat.PrintError(ChatGui, "Incorrect safeword.");
                return;
            }

            _timer.AddHours(Configuration.SafewordHours);
            // Confirmation only — never print remaining time to the doll.
            PluginChat.Print(ChatGui, "Safeword used.", PluginChat.Green);
            return;
        }

        PluginChat.PrintError(ChatGui, "Unknown command. Try /windup, /windup safeword <word>, or /windup unlock.");
    }
}
