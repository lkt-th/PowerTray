using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PowerTray;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Only one copy in the tray
        using var mutex = new Mutex(true, "PowerTray.SingleInstance", out bool isNew);
        if (!isNew) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext(args));
    }
}

sealed class TrayContext : ApplicationContext
{
    readonly NotifyIcon _tray;
    readonly PowerPopup _popup;
    readonly ShutdownTimer _timer = new();
    readonly ToolStripMenuItem _cancelTimerItem;
    readonly IReadOnlyList<string> _actionLabels;
    SettingsDialog? _settings;

    public TrayContext(string[] args)
    {
        const string saveWork = "Save any open work before continuing.";
        var actions = new List<PowerAction>
        {
            new("", "Lock", "Lock this PC?", "You'll need to sign in again to get back.", () => LockWorkStation()),
            new("", "Sign out", "Sign out now?", saveWork, () => Run("shutdown", "/l")),
            new("", "Sleep", "Put the PC to sleep?", "Your apps stay open in memory.", () => SetSuspendState(false, false, false)),
        };
        if (IsPwrHibernateAllowed())
            actions.Add(new("", "Hibernate", "Hibernate the PC?", "Your session is saved to disk.", () => SetSuspendState(true, false, false)));
        actions.Add(new("", "Restart", "Restart now?", saveWork, () => Run("shutdown", "/r /t 0")));
        actions.Add(new("", "Shut down", "Shut down now?", saveWork, () => Run("shutdown", "/s /t 0")));

        _actionLabels = actions.Select(a => a.Label).ToList();
        _popup = new PowerPopup(actions, _timer, ShowSettings);

        // Right-click menu
        var menu = new ContextMenuStrip();
        _cancelTimerItem = new ToolStripMenuItem("Cancel shutdown timer") { Visible = false };
        _cancelTimerItem.Click += (_, _) => _timer.Cancel();
        menu.Items.Add(_cancelTimerItem);
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add("Exit PowerTray", null, (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            Icon = CreatePowerIcon(TaskbarColor()),
            Text = "Power",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) _popup.Toggle(Cursor.Position);
        };

        // Redraw the icon when the taskbar switches between light and dark
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;

        _timer.Changed += () =>
        {
            _cancelTimerItem.Visible = _timer.Active;
            UpdateTrayIcon();
            UpdateTooltip();
        };
        _timer.Tick += UpdateTooltip;
        _timer.Warning += () => _tray.ShowBalloonTip(10000, "PowerTray",
            "Your PC will shut down in 1 minute. Save your work.", ToolTipIcon.Warning);
        // The timer usually runs unattended, so it forces apps closed instead of waiting on them
        var timedShutdown = new PowerAction("", "Shut down", "Time's up. Shut down now?",
            "Open apps will close without saving.", () => Run("shutdown", "/s /f /t 0"));
        _timer.FinalWarning += () => _popup.ConfirmTimedShutdown(timedShutdown);
        _timer.Elapsed += timedShutdown.Run;

        // --show opens the popup, --settings opens the settings dialog (handy for shortcuts)
        if (args.Contains("--show") || args.Contains("--settings"))
        {
            var timer = new System.Windows.Forms.Timer { Interval = 300 };
            timer.Tick += (_, _) =>
            {
                timer.Dispose();
                if (args.Contains("--settings")) { ShowSettings(); return; }
                var wa = Screen.PrimaryScreen!.WorkingArea;
                _popup.Toggle(new Point(wa.Right, wa.Bottom));
            };
            timer.Start();
        }
    }

    void ShowSettings()
    {
        if (_settings is { IsDisposed: false })
        {
            _settings.Activate();
            return;
        }
        _settings = new SettingsDialog(CreatePowerIcon(Palette.Current().Text), _actionLabels, ExitThread, _popup.RenderPreview);
        _settings.Show();
        _settings.Activate();
    }

    void OnPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e) => UpdateTrayIcon();

    void UpdateTrayIcon()
    {
        var old = _tray.Icon;
        _tray.Icon = CreatePowerIcon(TaskbarColor(), badge: _timer.Active);
        old?.Dispose();
    }

    void UpdateTooltip() =>
        _tray.Text = _timer.Active ? $"Power · Shutting down in {ShutdownTimer.Format(_timer.Remaining)}" : "Power";

    protected override void ExitThreadCore()
    {
        SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
        _tray.Visible = false;
        _tray.Dispose();
        _settings?.Close();
        _popup.Dispose();
        _timer.Dispose();
        base.ExitThreadCore();
    }

    static void Run(string file, string args) =>
        Process.Start(new ProcessStartInfo(file, args) { CreateNoWindow = true, UseShellExecute = false });

    static Color TaskbarColor() => Palette.LightTaskbar() ? Color.Black : Color.White;

    static Icon CreatePowerIcon(Color color, bool badge = false)
    {
        const int size = 64;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        using (var pen = new Pen(color, 7) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // Ring with a gap at the top, plus the vertical bar
            g.DrawArc(pen, 10, 12, 44, 44, -55, 290);
            g.DrawLine(pen, 32, 5, 32, 30);

            // Accent dot while a shutdown timer is running
            if (badge)
                using (var b = new SolidBrush(Palette.SystemAccent()))
                    g.FillEllipse(b, 40, 40, 24, 24);
        }
        var hIcon = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(hIcon).Clone();
        DestroyIcon(hIcon);
        return icon;
    }

    [DllImport("user32.dll")] static extern bool LockWorkStation();
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("powrprof.dll")] static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
    [DllImport("powrprof.dll")] static extern bool IsPwrHibernateAllowed();
}
