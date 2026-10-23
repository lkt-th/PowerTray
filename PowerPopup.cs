using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using static PowerTray.Ui;

namespace PowerTray;

record PowerAction(string Glyph, string Label, string Confirm, string Note, Action Run);

/// <summary>Windows 11 style flyout that slides up above the tray.</summary>
sealed class PowerPopup : Form
{
    // Layout in 96-DPI units, scaled by _s
    const int W = 360, Pad = 16, HeaderH = 72, Cols = 3, Gap = 8, TileH = 88, BarH = 44, FooterH = 52, ButtonH = 36;
    const int UsageTop = 68, UsageRowH = 28;
    static readonly int[] Presets = { 15, 30, 60, 120 };

    enum View { Grid, Confirm, Timer, ScreenTime }
    enum Kind { Tile, Yes, No, Gear, TimerBar, TimerCancel, Minus, Plus, Preset, Start, Back, UsageBar, DayPrev, DayNext }
    record Hit(Kind Kind, Rectangle Rect, PowerAction? Action = null, int Value = 0);

    readonly IReadOnlyList<PowerAction> _actions;
    readonly Action _openSettings;
    readonly ShutdownTimer _timer;
    readonly ScreenTime _screenTime;
    readonly Dictionary<string, Bitmap?> _appIcons = new(StringComparer.OrdinalIgnoreCase);
    readonly System.Windows.Forms.Timer _anim = new() { Interval = 10 };
    readonly System.Windows.Forms.Timer _confirmTick = new() { Interval = 50 };
    readonly List<Hit> _hits = new();
    readonly int _tilesH;

    Palette _p = Palette.Current();
    float _s = 1f;
    Font _title = null!, _big = null!, _body = null!, _small = null!, _smallIcon = null!, _icon = null!, _bigIcon = null!;
    Hit? _hover, _pressed;
    View _view;
    PowerAction? _confirming;
    int _timerMinutes = 30;
    Bitmap? _background;
    string? _backgroundKey;
    long _hiddenAt, _animStart, _confirmStart;
    int _confirmTimeout;
    bool _timedConfirm;
    int _fromTop, _toTop;
    DateOnly _usageDay, _firstUsageDay;
    IReadOnlyList<AppUsage> _usage = Array.Empty<AppUsage>();

    // The screen time bar only shows while tracking is on
    int ContentH => _tilesH + Gap + BarH + (Settings.ScreenTime ? Gap + BarH : 0);
    int FooterTop => HeaderH + ContentH + Pad;
    static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    public PowerPopup(IReadOnlyList<PowerAction> actions, ShutdownTimer timer, ScreenTime screenTime, Action openSettings)
    {
        _actions = actions;
        _openSettings = openSettings;
        _timer = timer;
        _screenTime = screenTime;
        int rows = (actions.Count + Cols - 1) / Cols;
        _tilesH = rows * TileH + (rows - 1) * Gap;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        TopMost = true;
        KeyPreview = true;
        DoubleBuffered = true;
        _anim.Tick += OnAnimTick;
        _confirmTick.Tick += OnConfirmTick;
        _timer.Tick += RefreshIfVisible;
        _timer.Changed += () =>
        {
            if (_timedConfirm && !_timer.Active) { HidePopup(); return; }
            LayoutHits();
            RefreshIfVisible();
        };
        _screenTime.Updated += () =>
        {
            if (!Visible) return;
            if (_view == View.ScreenTime && _usageDay == Today) _usage = _screenTime.Day(_usageDay);
            Invalidate();
        };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80;       // WS_EX_TOOLWINDOW: keep out of Alt+Tab
            cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
            return cp;
        }
    }

    public void Toggle(Point anchor)
    {
        if (Visible) { HidePopup(); return; }
        // The click on the tray icon that closed us via Deactivate should not reopen it
        if (Environment.TickCount64 - _hiddenAt < 300) return;
        ShowNear(anchor);
    }

    /// <summary>
    /// Asks before the shutdown timer goes off. The timer runs the shutdown itself if nobody answers,
    /// so this only counts down alongside it; Cancel stops the timer.
    /// </summary>
    public void ConfirmTimedShutdown(PowerAction action)
    {
        if (!Visible)
        {
            var wa = Screen.PrimaryScreen!.WorkingArea;
            ShowNear(new Point(wa.Right, wa.Bottom));
        }
        SetView(View.Confirm, action);
        _timedConfirm = true;
        _confirmTimeout = (int)ShutdownTimer.FinalWarningLead.TotalSeconds;
        _confirmTick.Start();
    }

    void ShowNear(Point anchor)
    {
        _s = MonitorDpi(anchor) / 96f;
        _p = Palette.Current();
        CreateFonts();
        _view = View.Grid;
        _confirming = null;
        _hover = _pressed = null;

        var size = new Size(S(W), S(FooterTop + FooterH));
        var wa = Screen.FromPoint(anchor).WorkingArea;
        int margin = S(12);
        int x = Math.Clamp(anchor.X - size.Width / 2, wa.Left + margin, wa.Right - size.Width - margin);
        bool taskbarOnTop = anchor.Y < wa.Top + wa.Height / 2;
        _toTop = taskbarOnTop ? wa.Top + margin : wa.Bottom - size.Height - margin;
        _fromTop = _toTop + S(taskbarOnTop ? -24 : 24);

        Bounds = new Rectangle(x, _fromTop, size.Width, size.Height);
        Opacity = Settings.Opacity;
        PrepareBackground(size);
        ApplyWindowTheme(Handle, _p, round: true);
        LayoutHits();
        Show();
        Activate();
        SetForegroundWindow(Handle);
        _animStart = Environment.TickCount64;
        _anim.Start();
    }

    /// <summary>Scales the chosen image to cover the popup; cached until the image or size changes.</summary>
    void PrepareBackground(Size size)
    {
        string? path = Settings.BackgroundPath;
        string? key = path == null ? null : $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}|{size}";
        if (key == _backgroundKey) return;

        _background?.Dispose();
        _background = null;
        _backgroundKey = key;
        if (path == null) return;
        try
        {
            using var img = Settings.LoadImage(path);
            _background = new Bitmap(size.Width, size.Height);
            using var g = Graphics.FromImage(_background);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            float scale = Math.Max(size.Width / (float)img.Width, size.Height / (float)img.Height);
            float w = img.Width * scale, h = img.Height * scale;
            g.DrawImage(img, (size.Width - w) / 2, (size.Height - h) / 2, w, h);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or OutOfMemoryException)
        {
            _background?.Dispose();
            _background = null;
        }
    }

    /// <summary>Surfaces let the background image show through a little.</summary>
    Color Glass(Color c) => _background == null ? c : Color.FromArgb(185, c);

    void HidePopup()
    {
        _anim.Stop();
        _confirmTick.Stop();
        _timedConfirm = false;
        Hide();
        _hiddenAt = Environment.TickCount64;
    }

    void RefreshIfVisible()
    {
        if (Visible) Invalidate();
    }

    void OnAnimTick(object? sender, EventArgs e)
    {
        double t = Math.Min(1, (Environment.TickCount64 - _animStart) / 200.0);
        double ease = 1 - Math.Pow(1 - t, 3);
        Top = (int)Math.Round(_fromTop + (_toTop - _fromTop) * ease);
        if (t >= 1) _anim.Stop();
    }

    /// <summary>Milliseconds left before the confirmation confirms itself.</summary>
    long ConfirmRemaining => _timedConfirm
        ? Math.Min(_confirmTimeout * 1000L, (long)_timer.Remaining.TotalMilliseconds)
        : _confirmTimeout * 1000L - (Environment.TickCount64 - _confirmStart);

    void OnConfirmTick(object? sender, EventArgs e)
    {
        if (!_timedConfirm && ConfirmRemaining <= 0) Run(_confirming!);
        else Invalidate();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        // Keep the timer's last warning up until it's answered or the time runs out
        if (Visible && !_timedConfirm) HidePopup();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Alt+F4 just hides; the app keeps running in the tray
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; HidePopup(); }
        base.OnFormClosing(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode != Keys.Escape) return;
        if (_timedConfirm) _timer.Cancel();
        else if (_view != View.Grid) SetView(View.Grid);
        else HidePopup();
    }

    // ---------- Layout & input ----------

    int S(int v) => (int)Math.Round(v * _s);
    Rectangle R(int x, int y, int w, int h) => new(S(x), S(y), S(w), S(h));

    void LayoutHits()
    {
        _hits.Clear();
        int fullW = W - Pad * 2, halfW = (fullW - Gap) / 2, buttonsY = HeaderH + ContentH - ButtonH;
        switch (_view)
        {
            case View.Grid:
                int tileW = (fullW - Gap * (Cols - 1)) / Cols;
                for (int i = 0; i < _actions.Count; i++)
                {
                    int c = i % Cols, r = i / Cols;
                    _hits.Add(new(Kind.Tile, R(Pad + c * (tileW + Gap), HeaderH + r * (TileH + Gap), tileW, TileH), _actions[i]));
                }
                int barY = HeaderH + _tilesH + Gap;
                _hits.Add(new(Kind.TimerBar, R(Pad, barY, fullW, BarH)));
                if (_timer.Active)
                    _hits.Add(new(Kind.TimerCancel, R(W - Pad - 6 - 72, barY + 6, 72, BarH - 12)));
                if (Settings.ScreenTime)
                    _hits.Add(new(Kind.UsageBar, R(Pad, barY + BarH + Gap, fullW, BarH)));
                break;

            case View.ScreenTime:
                if (_usageDay > _firstUsageDay) _hits.Add(new(Kind.DayPrev, R(Pad, HeaderH + 30, 32, 28)));
                if (_usageDay < Today) _hits.Add(new(Kind.DayNext, R(W - Pad - 32, HeaderH + 30, 32, 28)));
                _hits.Add(new(Kind.Back, R(Pad, buttonsY, fullW, ButtonH)));
                break;

            case View.Confirm:
                _hits.Add(new(Kind.Yes, R(Pad, buttonsY, halfW, ButtonH)));
                _hits.Add(new(Kind.No, R(Pad + halfW + Gap, buttonsY, halfW, ButtonH)));
                break;

            case View.Timer:
                _hits.Add(new(Kind.Minus, R(Pad, HeaderH + 36, 44, 44)));
                _hits.Add(new(Kind.Plus, R(W - Pad - 44, HeaderH + 36, 44, 44)));
                int chipW = (fullW - Gap * (Presets.Length - 1)) / Presets.Length;
                for (int i = 0; i < Presets.Length; i++)
                    _hits.Add(new(Kind.Preset, R(Pad + i * (chipW + Gap), HeaderH + 104, chipW, 32), Value: Presets[i]));
                _hits.Add(new(Kind.Start, R(Pad, buttonsY, halfW, ButtonH)));
                _hits.Add(new(Kind.Back, R(Pad + halfW + Gap, buttonsY, halfW, ButtonH)));
                break;
        }
        _hits.Add(new(Kind.Gear, R(W - Pad - 36, FooterTop + (FooterH - 36) / 2, 36, 36)));
    }

    Hit? HitAt(Point p) => _hits.LastOrDefault(h => h.Rect.Contains(p));
    Hit Find(Kind kind) => _hits.First(h => h.Kind == kind);

    void SetView(View view, PowerAction? confirming = null)
    {
        _view = view;
        _confirming = confirming;
        _timedConfirm = false;

        // With auto-confirm on, an unanswered confirmation runs the action on its own
        _confirmTick.Stop();
        _confirmTimeout = view == View.Confirm ? Settings.AutoConfirm : 0;
        if (_confirmTimeout > 0)
        {
            _confirmStart = Environment.TickCount64;
            _confirmTick.Start();
        }
        LayoutHits();
        _hover = HitAt(PointToClient(Cursor.Position));
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var h = HitAt(e.Location);
        if (h != _hover) { _hover = h; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = _pressed = null;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _pressed = HitAt(e.Location);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        var h = _pressed;
        _pressed = null;
        Invalidate();
        if (h != null && h == HitAt(e.Location)) Press(h);
    }

    void Press(Hit h)
    {
        switch (h.Kind)
        {
            case Kind.Tile when Settings.ShouldConfirm(h.Action!.Label):
                SetView(View.Confirm, h.Action);
                break;
            case Kind.Tile:
                Run(h.Action!);
                break;
            case Kind.Yes:
                var action = _confirming!;
                // Stop the timer so it doesn't run the shutdown a second time
                if (_timedConfirm) _timer.Cancel();
                Run(action);
                break;
            case Kind.No when _timedConfirm:
                _timer.Cancel();
                break;
            case Kind.No:
            case Kind.Back:
                SetView(View.Grid);
                break;
            case Kind.TimerBar:
                SetView(View.Timer);
                break;
            case Kind.UsageBar:
                _firstUsageDay = ScreenTime.FirstDay();
                _usageDay = Today;
                _usage = _screenTime.Day(_usageDay);
                SetView(View.ScreenTime);
                break;
            case Kind.DayPrev:
                ShowUsage(_usageDay.AddDays(-1));
                break;
            case Kind.DayNext:
                ShowUsage(_usageDay.AddDays(1));
                break;
            case Kind.TimerCancel:
                _timer.Cancel();
                break;
            case Kind.Minus:
                _timerMinutes = Math.Max(5, _timerMinutes - (_timerMinutes <= 60 ? 5 : 15));
                Invalidate();
                break;
            case Kind.Plus:
                _timerMinutes = Math.Min(12 * 60, _timerMinutes + (_timerMinutes < 60 ? 5 : 15));
                Invalidate();
                break;
            case Kind.Preset:
                _timerMinutes = h.Value;
                Invalidate();
                break;
            case Kind.Start:
                _timer.Start(TimeSpan.FromMinutes(_timerMinutes));
                SetView(View.Grid);
                break;
            case Kind.Gear:
                HidePopup();
                _openSettings();
                break;
        }
    }

    void ShowUsage(DateOnly day)
    {
        _usageDay = day;
        _usage = _screenTime.Day(day);
        LayoutHits();
        _hover = HitAt(PointToClient(Cursor.Position));
        Invalidate();
    }

    void Run(PowerAction action)
    {
        HidePopup();
        action.Run();
    }

    // ---------- Painting ----------

    /// <summary>Draws the popup's main view to an image at the given scale, for the settings preview.</summary>
    public Bitmap RenderPreview(float scale)
    {
        // Showing the popup again lays everything out from scratch, so borrowing its state is safe
        if (Visible) HidePopup();
        _s = scale;
        _p = Palette.Current();
        CreateFonts();
        _view = View.Grid;
        _confirming = null;
        _hover = _pressed = null;

        var size = new Size(S(W), S(FooterTop + FooterH));
        PrepareBackground(size);
        LayoutHits();
        var bmp = new Bitmap(size.Width, size.Height);
        using var g = Graphics.FromImage(bmp);
        Ui.Offscreen = true;
        try { Render(g, size); }
        finally { Ui.Offscreen = false; }
        return bmp;
    }

    protected override void OnPaint(PaintEventArgs e) => Render(e.Graphics, ClientSize);

    void Render(Graphics g, Size size)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(_p.Bg);
        if (_background != null)
        {
            g.DrawImageUnscaled(_background, 0, 0);
            // Tint so text stays readable on busy images
            using var tint = new SolidBrush(Color.FromArgb(120, _p.Bg));
            g.FillRectangle(tint, 0, 0, size.Width, size.Height);
        }

        // Header: avatar + user name
        string user = Environment.UserName;
        var avatar = R(Pad, 16, 40, 40);
        using (var b = new SolidBrush(_p.Accent)) g.FillEllipse(b, avatar);
        DrawText(g, user[..1].ToUpperInvariant(), _title, avatar, _p.OnAccent);
        DrawText(g, user, _title, R(Pad + 52, 16, W - Pad * 2 - 52, 22), _p.Text, TextFormatFlags.Left);
        DrawText(g, "Power options", _small, R(Pad + 52, 38, W - Pad * 2 - 52, 18), _p.SubText, TextFormatFlags.Left);

        switch (_view)
        {
            case View.Grid:
                foreach (var h in _hits.Where(h => h.Kind == Kind.Tile)) DrawTile(g, h);
                DrawTimerBar(g);
                if (Settings.ScreenTime) DrawUsageBar(g);
                break;
            case View.Confirm:
                DrawConfirm(g, _confirming!);
                break;
            case View.Timer:
                DrawTimerView(g);
                break;
            case View.ScreenTime:
                DrawUsageView(g);
                break;
        }

        // Footer strip with uptime and settings button
        int ft = S(FooterTop);
        using (var b = new SolidBrush(Glass(_p.Footer))) g.FillRectangle(b, 0, ft, size.Width, size.Height - ft);
        using (var pen = new Pen(_p.Divider)) g.DrawLine(pen, 0, ft, size.Width, ft);
        DrawText(g, Uptime(), _small, R(Pad, FooterTop, W - Pad * 2 - 44, FooterH), _p.SubText, TextFormatFlags.Left);
        DrawSubtleButton(g, Find(Kind.Gear), "", _smallIcon);
    }

    Color TileFill(Hit h) => Glass(h == _pressed ? _p.TilePressed : h == _hover ? _p.TileHover : _p.Tile);
    Color TileText(Hit h) => h == _pressed ? _p.SubText : _p.Text;

    void DrawTile(Graphics g, Hit h)
    {
        var r = h.Rect;
        FillRound(g, r, S(6), TileFill(h), _p.Border);
        DrawText(g, h.Action!.Glyph, _icon, new Rectangle(r.X, r.Y + S(16), r.Width, S(30)), TileText(h));
        DrawText(g, h.Action.Label, _body, new Rectangle(r.X + S(4), r.Y + S(52), r.Width - S(8), S(24)), TileText(h));
    }

    void DrawTimerBar(Graphics g)
    {
        var bar = Find(Kind.TimerBar);
        var r = bar.Rect;
        FillRound(g, r, S(6), TileFill(bar), _p.Border);
        DrawText(g, "", _smallIcon, new Rectangle(r.X + S(8), r.Y, S(32), r.Height), _timer.Active ? _p.Accent : TileText(bar));

        var textRect = new Rectangle(r.X + S(44), r.Y, r.Width - S(44 + 90), r.Height);
        if (_timer.Active)
        {
            DrawText(g, $"Shutting down in {ShutdownTimer.Format(_timer.Remaining)}", _body, textRect, _p.Text, TextFormatFlags.Left);
            var cancel = Find(Kind.TimerCancel);
            FillRound(g, cancel.Rect, S(4), Glass(cancel == _pressed ? _p.TilePressed : cancel == _hover ? _p.Bg : _p.Tile), _p.Border);
            DrawText(g, "Cancel", _small, cancel.Rect, TileText(cancel));
        }
        else
        {
            DrawText(g, "Shutdown timer", _body, textRect, TileText(bar), TextFormatFlags.Left);
            DrawText(g, "", _smallIcon, new Rectangle(r.Right - S(40), r.Y, S(32), r.Height), _p.SubText);
        }
    }

    void DrawUsageBar(Graphics g)
    {
        var bar = Find(Kind.UsageBar);
        var r = bar.Rect;
        FillRound(g, r, S(6), TileFill(bar), _p.Border);
        DrawText(g, "\uE7F4", _smallIcon, new Rectangle(r.X + S(8), r.Y, S(32), r.Height), TileText(bar));
        DrawText(g, "Screen time", _body, new Rectangle(r.X + S(44), r.Y, r.Width - S(44 + 140), r.Height), TileText(bar), TextFormatFlags.Left);
        int total = _screenTime.Day(Today).Sum(u => u.Seconds);
        DrawText(g, $"Today {ScreenTime.Format(total)}", _small, new Rectangle(r.Right - S(40 + 100), r.Y, S(100), r.Height), _p.SubText, TextFormatFlags.Right);
        DrawText(g, "\uE76C", _smallIcon, new Rectangle(r.Right - S(40), r.Y, S(32), r.Height), _p.SubText);
    }

    void DrawUsageView(Graphics g)
    {
        int w = W - Pad * 2;
        DrawText(g, "Screen time", _title, R(Pad, HeaderH, w, 24), _p.Text, TextFormatFlags.Left);
        DrawText(g, ScreenTime.Format(_usage.Sum(u => u.Seconds)), _title, R(Pad, HeaderH, w, 24), _p.Text, TextFormatFlags.Right);

        // Day picker, with arrows only where there is a day to go to
        string day = _usageDay == Today ? "Today"
            : _usageDay == Today.AddDays(-1) ? "Yesterday"
            : _usageDay.ToString("ddd d MMM", CultureInfo.InvariantCulture);
        DrawText(g, day, _body, R(Pad + 36, HeaderH + 30, w - 72, 28), _p.Text);
        foreach (var (kind, glyph, x) in new[] { (Kind.DayPrev, "\uE76B", Pad), (Kind.DayNext, "\uE76C", W - Pad - 32) })
        {
            if (_hits.FirstOrDefault(h => h.Kind == kind) is { } h) DrawButton(g, h, glyph, _smallIcon);
            else DrawText(g, glyph, _smallIcon, R(x, HeaderH + 30, 32, 28), _p.Divider);
        }

        int rowsTop = HeaderH + UsageTop, maxRows = (ContentH - ButtonH - Gap - UsageTop) / UsageRowH;
        if (_usage.Count == 0)
        {
            DrawText(g, "Nothing recorded for this day", _small, R(Pad, rowsTop, w, UsageRowH * 2), _p.SubText);
        }
        else
        {
            // Past the room we have, the rest go into one "Other apps" row
            var rows = _usage.Count <= maxRows ? _usage.ToList()
                : _usage.Take(maxRows - 1).Append(new AppUsage("Other apps", null, _usage.Skip(maxRows - 1).Sum(u => u.Seconds))).ToList();
            int most = rows.Max(u => u.Seconds);
            for (int i = 0; i < rows.Count; i++)
            {
                var u = rows[i];
                int y = rowsTop + i * UsageRowH;
                if (AppIcon(u.Path) is { } icon)
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(icon, R(Pad, y + 6, 16, 16));
                }
                DrawText(g, u.Name, _small, R(Pad + 24, y, 116, UsageRowH), _p.Text, TextFormatFlags.Left);
                var track = R(Pad + 148, y + 11, w - 148 - 60, 6);
                int len = Math.Max(track.Height, (int)(track.Width * (long)u.Seconds / Math.Max(1, most)));
                FillRound(g, new Rectangle(track.X, track.Y, len, track.Height), track.Height / 2, _p.Accent);
                DrawText(g, ScreenTime.Format(u.Seconds), _small, R(W - Pad - 56, y, 56, UsageRowH), _p.SubText, TextFormatFlags.Right);
            }
        }

        DrawButton(g, Find(Kind.Back), "Back");
    }

    /// <summary>The app's own icon, cached; null when the file is gone or has none.</summary>
    Bitmap? AppIcon(string? path)
    {
        if (path == null) return null;
        if (_appIcons.TryGetValue(path, out var bmp)) return bmp;
        try
        {
            using var icon = File.Exists(path) ? Icon.ExtractAssociatedIcon(path) : null;
            bmp = icon?.ToBitmap();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { }
        return _appIcons[path] = bmp;
    }

    void DrawConfirm(Graphics g, PowerAction a)
    {
        int w = W - Pad * 2;
        DrawText(g, a.Glyph, _bigIcon, R(Pad, HeaderH + 16, w, 44), _p.Accent);
        DrawText(g, a.Confirm, _title, R(Pad, HeaderH + 72, w, 26), _p.Text);
        DrawText(g, a.Note, _small, R(Pad, HeaderH + 100, w, 20), _p.SubText);
        DrawButton(g, Find(Kind.No), "Cancel");

        if (_confirmTimeout <= 0)
        {
            DrawAccentButton(g, Find(Kind.Yes), a.Label);
            return;
        }
        // Shrinking bar plus seconds left on the confirm button
        long left = Math.Max(0, ConfirmRemaining);
        var track = R(Pad + 40, HeaderH + 140, w - 80, 3);
        FillRound(g, track, track.Height, Glass(_p.Border));
        int filled = (int)(track.Width * left / (_confirmTimeout * 1000.0));
        if (filled > 0) FillRound(g, new Rectangle(track.X, track.Y, filled, track.Height), track.Height, _p.Accent);
        DrawAccentButton(g, Find(Kind.Yes), $"{a.Label} ({(left + 999) / 1000})");
    }

    void DrawTimerView(Graphics g)
    {
        int w = W - Pad * 2;
        DrawText(g, "Shutdown timer", _title, R(Pad, HeaderH, w, 24), _p.Text, TextFormatFlags.Left);

        DrawButton(g, Find(Kind.Minus), "", _smallIcon);
        DrawButton(g, Find(Kind.Plus), "", _smallIcon);
        DrawText(g, ShutdownTimer.FormatMinutes(_timerMinutes), _big, R(Pad + 52, HeaderH + 34, w - 104, 28), _p.Text);
        var at = DateTime.Now.AddMinutes(_timerMinutes);
        DrawText(g, $"Shuts down at {at:HH:mm}", _small, R(Pad + 52, HeaderH + 62, w - 104, 18), _p.SubText);

        foreach (var chip in _hits.Where(h => h.Kind == Kind.Preset))
        {
            if (chip.Value == _timerMinutes)
            {
                FillRound(g, chip.Rect, S(16), _p.Accent);
                DrawText(g, ShutdownTimer.FormatMinutes(chip.Value), _small, chip.Rect, _p.OnAccent);
            }
            else
            {
                FillRound(g, chip.Rect, S(16), TileFill(chip), _p.Border);
                DrawText(g, ShutdownTimer.FormatMinutes(chip.Value), _small, chip.Rect, TileText(chip));
            }
        }

        DrawAccentButton(g, Find(Kind.Start), _timer.Active ? "Reset timer" : "Start timer");
        DrawButton(g, Find(Kind.Back), "Cancel");
    }

    void DrawButton(Graphics g, Hit h, string text, Font? font = null)
    {
        FillRound(g, h.Rect, S(4), TileFill(h), _p.Border);
        DrawText(g, text, font ?? _body, h.Rect, TileText(h));
    }

    void DrawAccentButton(Graphics g, Hit h, string text)
    {
        FillRound(g, h.Rect, S(4), h == _pressed || h == _hover ? _p.AccentHover : _p.Accent);
        DrawText(g, text, _body, h.Rect, _p.OnAccent);
    }

    void DrawSubtleButton(Graphics g, Hit h, string glyph, Font font)
    {
        if (h == _hover || h == _pressed)
            FillRound(g, h.Rect, S(4), Glass(h == _pressed ? _p.TilePressed : _p.TileHover));
        DrawText(g, glyph, font, h.Rect, TileText(h));
    }

    static string Uptime()
    {
        var t = TimeSpan.FromMilliseconds(Environment.TickCount64);
        string days = t.Days > 0 ? $"{t.Days}d " : "";
        return $"Uptime: {days}{t.Hours}h {t.Minutes}m";
    }

    Font[] AllFonts => new[] { _title, _big, _body, _small, _smallIcon, _icon, _bigIcon };

    void CreateFonts()
    {
        foreach (var f in AllFonts) f?.Dispose();
        string text = TextFont, icons = IconFont;
        _title = Px(text, 15, FontStyle.Bold);
        _big = Px(text, 22, FontStyle.Bold);
        _body = Px(text, 13);
        _small = Px(text, 12);
        _smallIcon = Px(icons, 16);
        _icon = Px(icons, 22);
        _bigIcon = Px(icons, 34);
    }

    Font Px(string family, float px, FontStyle style = FontStyle.Regular) =>
        new(family, px * _s, style, GraphicsUnit.Pixel);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _anim.Dispose();
            _confirmTick.Dispose();
            _background?.Dispose();
            foreach (var icon in _appIcons.Values) icon?.Dispose();
            foreach (var f in AllFonts) f?.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
}

/// <summary>Colors close to the Windows 11 taskbar flyouts, following the system light/dark setting.</summary>
sealed record Palette(
    bool Dark, Color Bg, Color Footer, Color Divider,
    Color Tile, Color TileHover, Color TilePressed, Color Border,
    Color Text, Color SubText, Color Accent, Color AccentHover, Color OnAccent)
{
    public static bool LightTaskbar() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "SystemUsesLightTheme", 0) is int v && v == 1;

    public static Palette Current()
    {
        var accent = SystemAccent();
        return LightTaskbar()
            ? new(false, Hex(0xF3F3F3), Hex(0xEBEBEB), Hex(0xE0E0E0),
                Hex(0xFBFBFB), Hex(0xF6F6F6), Hex(0xF0F0F0), Hex(0xE5E5E5),
                Hex(0x1B1B1B), Hex(0x616161), Mix(accent, Color.Black, 0.15), Mix(accent, Color.Black, 0.25), Color.White)
            : new(true, Hex(0x2B2B2B), Hex(0x202020), Hex(0x1C1C1C),
                Hex(0x373737), Hex(0x3D3D3D), Hex(0x323232), Hex(0x404040),
                Color.White, Hex(0xC8C8C8), Mix(accent, Color.White, 0.4), Mix(accent, Color.White, 0.3), Color.Black);
    }

    public static Color SystemAccent() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null) is int abgr
            ? Color.FromArgb(abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF)
            : Hex(0x0078D4);

    static Color Hex(int rgb) => Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

    static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
}
