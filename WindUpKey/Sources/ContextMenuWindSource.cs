using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;
using WindUpKey.Protocol;
using WindUpKey.Services;

namespace WindUpKey.Sources;

public sealed class ContextMenuWindSource : IWindUpSource
{
    private static readonly double[] HourOptions = [1, 6, 12, 24];

    private readonly IContextMenu _contextMenu;
    private readonly IClientState _clientState;
    private readonly Configuration _config;
    private readonly RelayClient _relay;
    private readonly IPluginLog _log;
    private bool _enabled;

    public ContextMenuWindSource(
        IContextMenu contextMenu,
        IClientState clientState,
        Configuration config,
        RelayClient relay,
        IPluginLog log)
    {
        _contextMenu = contextMenu;
        _clientState = clientState;
        _config = config;
        _relay = relay;
        _log = log;
    }

    public void Enable()
    {
        if (_enabled)
            return;
        _contextMenu.OnMenuOpened += OnMenuOpened;
        _enabled = true;
    }

    public void Dispose()
    {
        if (!_enabled)
            return;
        _contextMenu.OnMenuOpened -= OnMenuOpened;
        _enabled = false;
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        // Winders and dolls can wind others (doll-to-doll included).
        if (!_config.HasChosenRole)
            return;

        if (args.MenuType != ContextMenuType.Default)
            return;

        if (args.Target is not MenuTargetDefault target)
            return;

        if (!TryGetPlayerIdentity(target, out var identity))
            return;

#if WINDUP_TESTING
        // Testing: self allowed when paired with your own key (same menu as any other pair).
        var selfTarget = string.Equals(identity, _relay.LocalIdentity, StringComparison.OrdinalIgnoreCase);
        if (selfTarget)
        {
            if (!_config.IsPairedByKey(_config.PairingKey) && !_config.IsPaired(identity))
                return;
        }
        else if (!_config.IsPaired(identity))
        {
            return;
        }
#else
        if (string.Equals(identity, _relay.LocalIdentity, StringComparison.OrdinalIgnoreCase))
            return;
        if (!_config.IsPaired(identity))
            return;
#endif

        const string menuTitle = "Wind Up";

        args.AddMenuItem(new MenuItem
        {
            Name = menuTitle,
            IsSubmenu = true,
            UseDefaultPrefix = true,
            OnClicked = clicked =>
            {
                var items = new List<IMenuItem>();
                foreach (var hours in HourOptions)
                {
                    var h = hours;
                    items.Add(new MenuItem
                    {
                        Name = $"{h} hour{(h == 1 ? string.Empty : "s")}",
                        UseDefaultPrefix = true,
                        OnClicked = _ => StartWind(identity, h),
                    });
                }

                items.Add(new MenuItem
                {
                    Name = "Unwind",
                    UseDefaultPrefix = true,
                    OnClicked = _ => StartUnwind(identity),
                });

                clicked.OpenSubmenu(menuTitle, items);
            },
        });
    }

    private void StartWind(string identity, double hours)
    {
        _ = SendWindSafeAsync(identity, hours);
    }

    private void StartUnwind(string identity)
    {
        _ = SendUnwindSafeAsync(identity);
    }

    private async Task SendWindSafeAsync(string identity, double hours)
    {
        try
        {
            await _relay.SendWindAsync(identity, hours);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to send wind to {Identity}", identity);
        }
    }

    private async Task SendUnwindSafeAsync(string identity)
    {
        try
        {
            await _relay.SendUnwindAsync(identity);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to send unwind to {Identity}", identity);
        }
    }

    private bool TryGetPlayerIdentity(MenuTargetDefault target, out string identity)
    {
        identity = string.Empty;
        var name = target.TargetName;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        // Prefer home world from context; fall back to object if available.
        string? world = null;
        try
        {
            world = target.TargetHomeWorld.ValueNullable?.Name.ToString();
        }
        catch (Exception ex)
        {
            _log.Verbose(ex, "TargetHomeWorld unavailable");
        }

        if (string.IsNullOrEmpty(world) && target.TargetObject is IPlayerCharacter pc)
            world = pc.HomeWorld.ValueNullable?.Name.ToString();

        if (string.IsNullOrEmpty(world))
            return false;

        // ContentId 0 and no character often means NPC; require a player-like target.
        if (target.TargetObject is not null and not IPlayerCharacter)
            return false;

        identity = PlayerIdentity.Format(name, world);
        return true;
    }
}
