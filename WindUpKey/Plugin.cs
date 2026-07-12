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

    public Configuration Configuration { get; }
    private readonly WindowSystem _windowSystem = new("WindUpKey");
    private readonly ConfigWindow _configWindow;
    private readonly LockController _lockController;
    private readonly WindTimerService _timer;
    private readonly LowWindWarningService _lowWind;
    private readonly ConsentService _consent;
    private readonly IWindNotifier _notifier;
    private readonly RelayClient _relay;
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

        var commands = new GameCommandRunner(Log);
        _lockController = new LockController(GameInterop, ClientState, Condition, ObjectTable, commands, Configuration, Log);
        var lowWindMessages = new LowWindMessagesConfig(PluginInterface.GetPluginConfigDirectory(), Log);
        _lowWind = new LowWindWarningService(Configuration, ChatGui, lowWindMessages);
        _timer = new WindTimerService(Configuration, _lockController, commands, ObjectTable, Condition, _lowWind);
        _consent = new ConsentService(Configuration);
        _notifier = new ChatWindNotifier(ChatGui);
        _relay = new RelayClient(Configuration, ClientState, ObjectTable, Log, _consent, _timer, _notifier);

        _configWindow = new ConfigWindow(Configuration, _relay, _timer, TargetManager, lowWindMessages.FilePath);
        _windowSystem.AddWindow(_configWindow);

        var contextMenuSource = new ContextMenuWindSource(ContextMenu, ClientState, Configuration, _relay, Log);
        _sources.Add(contextMenuSource);
        foreach (var source in _sources)
            source.Enable();

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage =
                "Open Wind-Up Key config. /windup safeword <word> uses your safeword. /windup unlock clears Hardcore. /windup check and /windup debug (debug mode) show low-wind status.",
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
            _lowWind.OnLoggedIn();
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
        _lockController.Dispose();
        _windowSystem.RemoveAllWindows();
        _configWindow.Dispose();
    }

    private void OpenConfig() => _configWindow.IsOpen = true;

    private void OnFrameworkUpdate(IFramework framework)
    {
        _relay.Tick();
        _timer.Tick();
        _lowWind.Tick();
        _lockController.Tick();
    }

    private void OnLogin()
    {
        _relay.Start();
        _timer.OnLoggedIn();
        _lowWind.OnLoggedIn();
    }

    private void OnLogout(int type, int code) => _relay.Stop();

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

        if (string.Equals(verb, "unlock", StringComparison.OrdinalIgnoreCase))
        {
            if (!Configuration.HardcoreMode)
            {
                ChatGui.Print("[Wind-Up Key] Hardcore is already off.");
                return;
            }

            Configuration.HardcoreMode = false;
            Configuration.Save();
            ChatGui.Print("[Wind-Up Key] Hardcore cleared. You can change role again.");
            return;
        }

        if (string.Equals(verb, "check", StringComparison.OrdinalIgnoreCase))
        {
            if (!Configuration.DebugMode)
            {
                ChatGui.PrintError("[Wind-Up Key] Enable debug mode in config to use /windup check.");
                return;
            }

            _lowWind.PrintCheckStatus();
            return;
        }

        if (string.Equals(verb, "debug", StringComparison.OrdinalIgnoreCase))
        {
            if (!Configuration.DebugMode)
            {
                ChatGui.PrintError("[Wind-Up Key] Enable debug mode in config to use /windup debug.");
                return;
            }

            _lowWind.PrintDebugStatus();
            return;
        }

        if (string.Equals(verb, "safeword", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(rest))
            {
                ChatGui.PrintError("[Wind-Up Key] Usage: /windup safeword <word>");
                return;
            }

            if (!Configuration.IsDoll)
            {
                ChatGui.PrintError("[Wind-Up Key] Safeword is only available in Doll mode.");
                return;
            }

            if (!Configuration.SafewordEnabled)
            {
                ChatGui.PrintError("[Wind-Up Key] Safeword is disabled in config.");
                return;
            }

            if (!string.Equals(rest, Configuration.Safeword, StringComparison.Ordinal))
            {
                ChatGui.PrintError("[Wind-Up Key] Incorrect safeword.");
                return;
            }

            _timer.AddHours(Configuration.SafewordHours);
            // Confirmation only — never print remaining time to the doll.
            ChatGui.Print("[Wind-Up Key] Safeword used.");
            return;
        }

        ChatGui.PrintError("[Wind-Up Key] Unknown command. Try /windup, /windup safeword <word>, or /windup unlock.");
    }
}
