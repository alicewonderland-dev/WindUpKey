using System;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Plugin.Services;

namespace WindUpKey.Services;

/// <summary>
/// The spare springs on Dalamud's server info bar, when Settings, Interface turns it on. Clicking the entry opens (or
/// closes) the main window. The entry is made once and then only shown or hidden, and removed when the plugin unloads.
/// </summary>
public sealed class ServerInfoBarService : IDisposable
{
    /// <summary>The entry's title, as Dalamud's own settings list it (where players can also hide it).</summary>
    public const string Title = "Wind-Up Key";

    private readonly IDtrBar _dtrBar;
    private readonly Configuration _config;
    private readonly RelayClient _relay;
    private readonly Action _toggleMainWindow;
    private IDtrBarEntry? _entry;

    /// <summary>The text last set, so the entry is only changed when the springs do.</summary>
    private string? _shownText;

    public ServerInfoBarService(IDtrBar dtrBar, Configuration config, RelayClient relay, Action toggleMainWindow)
    {
        _dtrBar = dtrBar;
        _config = config;
        _relay = relay;
        _toggleMainWindow = toggleMainWindow;
    }

    /// <summary>Framework thread, each tick: shows or hides the entry and keeps its springs current.</summary>
    public void Update()
    {
        var show = _config.ShowSpringsInServerInfoBar && _config.HasChosenRole;
        if (!show)
        {
            if (_entry is not null)
                _entry.Shown = false;
            return;
        }

        if (_entry is null)
        {
            _entry = _dtrBar.Get(Title);
            // A left click only (Dalamud also reports right clicks here).
            _entry.OnClick = click =>
            {
                if (click.ClickType == MouseClickType.Left)
                    _toggleMainWindow();
            };
        }

        _entry.Shown = true;
        var text = SpringsText(_relay.SpringBalance, _relay.SpringCap);
        if (text == _shownText)
            return;

        _shownText = text;
        _entry.Text = text;
        _entry.Tooltip = _relay.SpringBalance is { } balance
            ? $"Wind-Up Key: {balance} of {_relay.SpringCap} spare springs. Click to open."
            : "Wind-Up Key: spare springs not known yet. Click to open.";
    }

    /// <summary>"Springs 7/10", or "Springs …" before the relay has said.</summary>
    internal static string SpringsText(int? balance, int cap) => balance is { } b ? $"Springs {b}/{cap}" : "Springs …";

    public void Dispose()
    {
        _entry?.Remove();
        _entry = null;
    }
}
