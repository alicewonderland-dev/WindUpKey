using System.Diagnostics;

namespace WindUpRelay.Host;

internal sealed class MainForm : Form
{
    private readonly HostController _host = new();
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _trayMenu;
    private readonly Label _relayStatus;
    private readonly Label _funnelStatus;
    private readonly Label _tokenStatus;
    private readonly TextBox _wssUrl;
    private readonly TextBox _log;
    private readonly Button _startButton;
    private readonly Button _stopButton;
    private readonly Button _copyButton;
    private bool _exitRequested;

    public MainForm()
    {
        Text = "Wind-Up Key Host";
        Width = 520;
        Height = 440;
        MinimumSize = new Size(440, 340);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        ShowInTaskbar = true;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 7,
            Padding = new Padding(12),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _tokenStatus = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        _relayStatus = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        _funnelStatus = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };

        var urlRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        urlRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        urlRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        _wssUrl = new TextBox { Dock = DockStyle.Fill, ReadOnly = true };
        _copyButton = new Button { Text = "Copy", Dock = DockStyle.Fill };
        _copyButton.Click += (_, _) => CopyWssUrl();
        urlRow.Controls.Add(_wssUrl, 0, 0);
        urlRow.Controls.Add(_copyButton, 1, 0);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };
        _startButton = new Button { Text = "Start", Width = 100, Height = 28 };
        _stopButton = new Button { Text = "Stop", Width = 100, Height = 28, Enabled = false };
        _startButton.Click += async (_, _) => await StartHostAsync();
        _stopButton.Click += async (_, _) => await StopHostAsync();
        buttons.Controls.Add(_startButton);
        buttons.Controls.Add(_stopButton);

        var logLabel = new Label
        {
            Text = "Status log (host-only; players never see this)",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _log = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font(FontFamily.GenericMonospace, 8.25f),
        };

        layout.Controls.Add(_tokenStatus, 0, 0);
        layout.Controls.Add(_relayStatus, 0, 1);
        layout.Controls.Add(_funnelStatus, 0, 2);
        layout.Controls.Add(urlRow, 0, 3);
        layout.Controls.Add(buttons, 0, 4);
        layout.Controls.Add(logLabel, 0, 5);
        layout.Controls.Add(_log, 0, 6);
        Controls.Add(layout);

        _trayMenu = new ContextMenuStrip();
        _trayMenu.Items.Add("Open", null, (_, _) => RestoreFromTray());
        _trayMenu.Items.Add("Start", null, async (_, _) => await StartHostAsync());
        _trayMenu.Items.Add("Stop", null, async (_, _) => await StopHostAsync());
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("Exit", null, (_, _) => ExitApplication());

        _tray = new NotifyIcon
        {
            Text = "Wind-Up Key Host",
            Icon = SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = _trayMenu,
        };
        _tray.DoubleClick += (_, _) => RestoreFromTray();

        _host.StateChanged += () => Ui(RefreshStatus);
        _host.LogLine += line => Ui(() => AppendLog(line));
        _host.TunnelFailed += message => Ui(() =>
        {
            AppendLog("ERROR: " + message.ReplaceLineEndings(" | "));
            _tray.ShowBalloonTip(
                5000,
                "Wind-Up Key Host",
                "Host failed — open the window for details.",
                ToolTipIcon.Error);
            RefreshStatus();
        });

        Resize += OnResizeToTray;
        FormClosing += OnFormClosing;

        RefreshStatus();
        AppendLog("Ready. Start runs the local relay and Tailscale Funnel.");
        AppendLog("Close or minimize to send this window to the tray.");
    }

    private void Ui(Action action)
    {
        if (IsDisposed)
            return;
        try
        {
            if (!IsHandleCreated)
            {
                action();
                return;
            }

            if (InvokeRequired)
                BeginInvoke(action);
            else
                action();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private async Task StartHostAsync()
    {
        try
        {
            _startButton.Enabled = false;
            await _host.StartAsync();
            if (!string.IsNullOrEmpty(_host.PluginWssUrl))
            {
                _tray.ShowBalloonTip(
                    4000,
                    "Wind-Up Key Host",
                    "Funnel is up. Copy wss URL into RelayDefaults if needed.",
                    ToolTipIcon.Info);
            }
        }
        catch (Exception ex)
        {
            AppendLog("ERROR: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Wind-Up Key Host", MessageBoxButtons.OK, MessageBoxIcon.Error);
            await _host.StopAsync();
        }
        finally
        {
            RefreshStatus();
        }
    }

    private async Task StopHostAsync()
    {
        _stopButton.Enabled = false;
        await _host.StopAsync();
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var tokenOk = _host.HasRelayTokenConfigured();
        _tokenStatus.Text = tokenOk
            ? "Token: configured (appsettings.Production.json)"
            : "Token: missing — set Relay:Token in appsettings.Production.json";
        _tokenStatus.ForeColor = tokenOk ? Color.DarkGreen : Color.Firebrick;

        _relayStatus.Text = _host.RelayRunning ? "Relay: running (127.0.0.1:8787)" : "Relay: stopped";
        _funnelStatus.Text = _host.FunnelRunning
            ? (string.IsNullOrEmpty(_host.PublicHttpsUrl)
                ? "Funnel: enabled"
                : $"Funnel: {_host.PublicHttpsUrl}")
            : "Funnel: stopped";

        _wssUrl.Text = _host.PluginWssUrl;
        var busy = _host.IsRunning;
        _startButton.Enabled = !busy;
        _stopButton.Enabled = busy;
        _copyButton.Enabled = !string.IsNullOrEmpty(_host.PluginWssUrl);

        _tray.Text = busy
            ? (string.IsNullOrEmpty(_host.PluginWssUrl) ? "Wind-Up Key Host (starting…)" : "Wind-Up Key Host (online)")
            : "Wind-Up Key Host";
    }

    private void CopyWssUrl()
    {
        if (string.IsNullOrEmpty(_wssUrl.Text))
            return;
        Clipboard.SetText(_wssUrl.Text);
        AppendLog("Copied plugin wss URL (paste into RelayDefaults.cs once, then rebuild).");
        _tray.ShowBalloonTip(2000, "Wind-Up Key Host", "WSS URL copied.", ToolTipIcon.Info);
    }

    private void AppendLog(string line)
    {
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        _log.AppendText($"[{stamp}] {line}{Environment.NewLine}");
    }

    private void OnResizeToTray(object? sender, EventArgs e)
    {
        if (WindowState != FormWindowState.Minimized)
            return;

        HideToTray();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_exitRequested || e.CloseReason != CloseReason.UserClosing)
            return;

        e.Cancel = true;
        HideToTray();
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
        WindowState = FormWindowState.Normal;
        _tray.ShowBalloonTip(
            2500,
            "Wind-Up Key Host",
            "Still running in the tray. Double-click the icon to open.",
            ToolTipIcon.Info);
    }

    private void RestoreFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        _ = ExitAsync();
    }

    private async Task ExitAsync()
    {
        await _host.StopAsync();
        _tray.Visible = false;
        _tray.Dispose();
        _host.Dispose();
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _trayMenu.Dispose();
            _host.Dispose();
        }

        base.Dispose(disposing);
    }
}
