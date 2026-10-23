using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Microsoft.Win32;
using static PowerTray.Ui;

namespace PowerTray;

/// <summary>Windows 11 style settings window with a startup toggle and a transparency slider.</summary>
sealed class SettingsDialog : Form
{
    // Layout in 96-DPI units, scaled by _s
    const int W = 420, H = 616, Pad = 20, CardPad = 16, Step = 5, BgCardY = 204, ConfirmCardY = 284, PreviewW = 300;
    const int ConfirmCols = 3, ConfirmItemH = 32;
    const float PreviewScale = 0.66f;

    enum Kind { Startup, ScreenTime, Slider, Browse, Remove, Confirm, Timeout, Exit, Close }
    record Hit(Kind Kind, Rectangle Rect, string? Action = null, int Value = 0);

    readonly List<Hit> _hits = new();
    readonly IReadOnlyList<string> _actions;
    readonly Action _exitApp;
    readonly Func<float, Bitmap> _renderPreview;
    readonly Palette _p = Palette.Current();
    readonly float _s;
    readonly Font _body, _small, _check, _logo;
    readonly Bitmap _backdrop;
    Bitmap? _preview;
    Hit? _hover, _pressed;
    bool _dragging;

    /// <param name="actions">Labels of the popup's power buttons, for the confirmation choices.</param>
    public SettingsDialog(Icon icon, IReadOnlyList<string> actions, Action exitApp, Func<float, Bitmap> renderPreview)
    {
        _actions = actions;
        _exitApp = exitApp;
        _renderPreview = renderPreview;
        _s = MonitorDpi(Cursor.Position) / 96f;
        _body = new Font(TextFont, 14 * _s, GraphicsUnit.Pixel);
        _small = new Font(TextFont, 12 * _s, GraphicsUnit.Pixel);
        _check = new Font(IconFont, 12 * _s, GraphicsUnit.Pixel);
        _logo = new Font(IconFont, 20 * _s, GraphicsUnit.Pixel);

        Text = "PowerTray Settings";
        Icon = icon;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        KeyPreview = true;
        BackColor = _p.Bg;
        ClientSize = new Size(S(W + PreviewW + Pad), S(H));
        _backdrop = CreateBackdrop(PreviewRect().Size);
        _preview = _renderPreview(_s * PreviewScale);

        LayoutHits();
        Settings.Changed += OnSettingsChanged;
    }

    void LayoutHits()
    {
        _hits.Clear();
        _hits.Add(new(Kind.Startup, R(Pad, 20, W - Pad * 2, 68)));
        _hits.Add(new(Kind.Slider, R(Pad + CardPad, 96 + 58, W - Pad * 2 - CardPad * 2, 28)));
        int browseX = W - Pad - CardPad - 84;
        _hits.Add(new(Kind.Browse, R(browseX, BgCardY + 20, 84, 32)));
        if (Settings.BackgroundPath != null)
            _hits.Add(new(Kind.Remove, R(browseX - 8 - 76, BgCardY + 20, 76, 32)));
        int itemW = (W - Pad * 2 - CardPad * 2) / ConfirmCols;
        for (int i = 0; i < _actions.Count; i++)
        {
            int c = i % ConfirmCols, r = i / ConfirmCols;
            _hits.Add(new(Kind.Confirm, R(Pad + CardPad + c * itemW, ConfirmCardY + 62 + r * (ConfirmItemH + 4), itemW - 4, ConfirmItemH), _actions[i]));
        }
        int chipX = W - Pad - CardPad - Settings.AutoConfirmChoices.Length * 44 + 4;
        foreach (int seconds in Settings.AutoConfirmChoices)
        {
            _hits.Add(new(Kind.Timeout, R(chipX, TimeoutRowY, 40, 28), Value: seconds));
            chipX += 44;
        }
        _hits.Add(new(Kind.ScreenTime, R(Pad, ScreenTimeCardY, W - Pad * 2, 68)));
        _hits.Add(new(Kind.Exit, R(Pad, H - 52, 140, 32)));
        _hits.Add(new(Kind.Close, R(W - Pad - 100, H - 52, 100, 32)));
    }

    int S(int v) => (int)Math.Round(v * _s);
    Rectangle R(int x, int y, int w, int h) => new(S(x), S(y), S(w), S(h));
    Hit Find(Kind kind) => _hits.First(h => h.Kind == kind);
    Hit? HitAt(Point p) => _hits.LastOrDefault(h => h.Rect.Contains(p));

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyWindowTheme(Handle, _p, caption: _p.Bg);
    }

    void OnSettingsChanged()
    {
        _preview?.Dispose();
        _preview = _renderPreview(_s * PreviewScale);
        LayoutHits();
        Invalidate();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Settings.Changed -= OnSettingsChanged;
        _body.Dispose();
        _small.Dispose();
        _check.Dispose();
        _logo.Dispose();
        _backdrop.Dispose();
        _preview?.Dispose();
        Icon?.Dispose();
        base.OnFormClosed(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Close();
    }

    // ---------- Input ----------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging) { SetTransparencyFromX(e.X); return; }
        var h = HitAt(e.Location);
        if (h != _hover) { _hover = h; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_dragging) return;
        _hover = null;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _pressed = HitAt(e.Location);
        if (_pressed?.Kind == Kind.Slider)
        {
            _dragging = true;
            Capture = true;
            SetTransparencyFromX(e.X);
        }
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        var h = _pressed;
        _pressed = null;
        if (_dragging)
        {
            _dragging = false;
            Capture = false;
        }
        else if (h != null && h == HitAt(e.Location))
        {
            Press(h);
        }
        _hover = HitAt(e.Location);
        Invalidate();
    }

    void Press(Hit h)
    {
        switch (h.Kind)
        {
            case Kind.Startup:
                Settings.StartWithWindows = !Settings.StartWithWindows;
                break;
            case Kind.ScreenTime:
                Settings.ScreenTime = !Settings.ScreenTime;
                break;
            case Kind.Browse:
                PickBackground();
                break;
            case Kind.Remove:
                Settings.ClearBackground();
                break;
            case Kind.Confirm:
                Settings.SetConfirm(h.Action!, !Settings.ShouldConfirm(h.Action!));
                break;
            case Kind.Timeout:
                Settings.AutoConfirm = h.Value;
                break;
            case Kind.Exit:
                Close();
                _exitApp();
                break;
            case Kind.Close:
                Close();
                break;
        }
    }

    void PickBackground()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choose a background image",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            // Make sure it is a readable image before keeping it
            using (Settings.LoadImage(dialog.FileName)) { }
            Settings.SetBackground(dialog.FileName);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or OutOfMemoryException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "Couldn't open that image. Try a PNG or JPG file.", "PowerTray",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    (int Left, int Width) Track()
    {
        var r = Find(Kind.Slider).Rect;
        return (r.X + S(10), r.Width - S(20));
    }

    void SetTransparencyFromX(int x)
    {
        var (left, width) = Track();
        double t = Math.Clamp((x - left) / (double)width, 0, 1);
        int value = (int)Math.Round(t * Settings.MaxTransparency / Step) * Step;
        if (value != Settings.Transparency) Settings.Transparency = value;
    }

    // ---------- Painting ----------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(_p.Bg);
        DrawToggleCard(g, Find(Kind.Startup), "Start with Windows", "Open automatically when you sign in", Settings.StartWithWindows);
        DrawTransparencyCard(g);
        DrawBackgroundCard(g);
        DrawConfirmCard(g);
        DrawToggleCard(g, Find(Kind.ScreenTime), "Screen time",
            "Track time per app, stays on this PC", Settings.ScreenTime);
        DrawButton(g, Find(Kind.Exit), "Exit PowerTray", accent: false);
        DrawButton(g, Find(Kind.Close), "Close", accent: true);
        DrawPreview(g);
        DrawAbout(g);
    }

    const int AboutH = 44;

    Rectangle PreviewRect() => R(W, 20, PreviewW, H - 40 - AboutH);

    /// <summary>Product name, version and credit under the preview, read from the assembly.</summary>
    void DrawAbout(Graphics g)
    {
        var asm = System.Reflection.Assembly.GetEntryAssembly();
        string version = Application.ProductVersion.Split('+')[0];
        string credit = asm?.GetCustomAttributes(typeof(System.Reflection.AssemblyCopyrightAttribute), false)
            .OfType<System.Reflection.AssemblyCopyrightAttribute>().FirstOrDefault()?.Copyright ?? "";

        int top = H - 20 - AboutH + 12;
        DrawText(g, "", _logo, R(W + 4, top, 24, 32), _p.Accent);
        DrawText(g, $"PowerTray  {version}", _body, R(W + 34, top - 1, PreviewW - 34, 18), _p.Text, TextFormatFlags.Left);
        DrawText(g, credit, _small, R(W + 34, top + 17, PreviewW - 34, 16), _p.SubText, TextFormatFlags.Left);
    }

    /// <summary>The popup as it will look, over the desktop wallpaper so transparency shows.</summary>
    void DrawPreview(Graphics g)
    {
        var panel = PreviewRect();
        using (var clip = RoundPath(panel, S(8)))
        {
            g.SetClip(clip);
            g.DrawImageUnscaled(_backdrop, panel.Location);
            g.ResetClip();
            using var pen = new Pen(_p.Border);
            g.DrawPath(pen, clip);
        }

        var chip = new Rectangle(panel.X + S(12), panel.Y + S(12), S(64), S(22));
        FillRound(g, chip, chip.Height / 2, Color.FromArgb(200, _p.Bg));
        DrawText(g, "Preview", _small, chip, _p.Text);

        if (_preview == null) return;
        var rect = new Rectangle(panel.X + (panel.Width - _preview.Width) / 2, panel.Y + S(44), _preview.Width, _preview.Height);
        using var attrs = new ImageAttributes();
        attrs.SetColorMatrix(new ColorMatrix { Matrix33 = (float)Settings.Opacity });
        using var popupClip = RoundPath(rect, S(6));
        g.SetClip(popupClip);
        g.DrawImage(_preview, rect, 0, 0, _preview.Width, _preview.Height, GraphicsUnit.Pixel, attrs);
        g.ResetClip();
        using (var pen = new Pen(Color.FromArgb(70, 0, 0, 0))) g.DrawPath(pen, popupClip);
    }

    Bitmap CreateBackdrop(Size size)
    {
        var bmp = new Bitmap(size.Width, size.Height);
        using var g = Graphics.FromImage(bmp);
        using var wallpaper = LoadWallpaper();
        if (wallpaper != null)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            float scale = Math.Max(size.Width / (float)wallpaper.Width, size.Height / (float)wallpaper.Height);
            float w = wallpaper.Width * scale, h = wallpaper.Height * scale;
            g.DrawImage(wallpaper, (size.Width - w) / 2, (size.Height - h) / 2, w, h);
        }
        else
        {
            using var brush = new LinearGradientBrush(new Rectangle(Point.Empty, size),
                _p.Accent, _p.Dark ? Color.Black : Color.White, 60f);
            g.FillRectangle(brush, 0, 0, size.Width, size.Height);
        }
        return bmp;
    }

    static Image? LoadWallpaper()
    {
        string?[] candidates =
        {
            Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WallPaper", null) as string,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Themes\TranscodedWallpaper"),
        };
        foreach (var path in candidates)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
            try { return Settings.LoadImage(path); }
            catch (Exception ex) when (ex is ArgumentException or IOException or OutOfMemoryException) { }
        }
        return null;
    }

    void DrawCardText(Graphics g, Rectangle card, string title, string subtitle, int reserve = 100)
    {
        int w = card.Width - S(CardPad * 2 + reserve);
        DrawText(g, title, _body, new Rectangle(card.X + S(CardPad), card.Y + S(14), w, S(20)), _p.Text, TextFormatFlags.Left);
        DrawText(g, subtitle, _small, new Rectangle(card.X + S(CardPad), card.Y + S(36), w, S(18)), _p.SubText, TextFormatFlags.Left);
    }

    void DrawToggleCard(Graphics g, Hit card, string title, string subtitle, bool on)
    {
        var r = card.Rect;
        var fill = card == _pressed ? _p.TilePressed : card == _hover ? _p.TileHover : _p.Tile;
        FillRound(g, r, S(6), fill, _p.Border);
        DrawCardText(g, r, title, subtitle);

        var toggle = new Rectangle(r.Right - S(CardPad + 40), r.Y + (r.Height - S(20)) / 2, S(40), S(20));
        DrawText(g, on ? "On" : "Off", _body, new Rectangle(toggle.X - S(44), toggle.Y, S(34), toggle.Height), _p.Text, TextFormatFlags.Right);
        int knob = S(card == _hover ? 14 : 12);
        int knobY = toggle.Y + (toggle.Height - knob) / 2;
        if (on)
        {
            FillRound(g, toggle, toggle.Height / 2, card == _hover ? _p.AccentHover : _p.Accent);
            using var b = new SolidBrush(_p.OnAccent);
            g.FillEllipse(b, toggle.Right - S(4) - knob, knobY, knob, knob);
        }
        else
        {
            FillRound(g, toggle, toggle.Height / 2, fill, _p.SubText);
            using var b = new SolidBrush(_p.SubText);
            g.FillEllipse(b, toggle.X + S(4), knobY, knob, knob);
        }
    }

    void DrawTransparencyCard(Graphics g)
    {
        var card = R(Pad, 96, W - Pad * 2, 100);
        FillRound(g, card, S(6), _p.Tile, _p.Border);
        DrawCardText(g, card, "Transparency", "How see-through the power menu is");

        int value = Settings.Transparency;
        DrawText(g, value == 0 ? "Off" : $"{value}%", _body,
            new Rectangle(card.Right - S(CardPad + 80), card.Y + S(14), S(80), S(20)), _p.Text, TextFormatFlags.Right);

        var slider = Find(Kind.Slider);
        var (left, width) = Track();
        int cy = slider.Rect.Y + slider.Rect.Height / 2;
        int thumbX = left + (int)Math.Round(width * value / (double)Settings.MaxTransparency);
        FillRound(g, new Rectangle(left, cy - S(2), width, S(4)), S(2), _p.SubText);
        if (thumbX > left) FillRound(g, new Rectangle(left, cy - S(2), thumbX - left, S(4)), S(2), _p.Accent);

        int outer = S(20), inner = S(_dragging || slider == _hover ? 14 : 12);
        using (var b = new SolidBrush(_p.Dark ? Color.FromArgb(0x45, 0x45, 0x45) : Color.White))
            g.FillEllipse(b, thumbX - outer / 2, cy - outer / 2, outer, outer);
        using (var pen = new Pen(_p.Border))
            g.DrawEllipse(pen, thumbX - outer / 2, cy - outer / 2, outer, outer);
        using (var b = new SolidBrush(_p.Accent))
            g.FillEllipse(b, thumbX - inner / 2, cy - inner / 2, inner, inner);
    }

    void DrawBackgroundCard(Graphics g)
    {
        var card = R(Pad, BgCardY, W - Pad * 2, 72);
        FillRound(g, card, S(6), _p.Tile, _p.Border);
        bool hasImage = Settings.BackgroundName is not null;
        DrawCardText(g, card, "Background image", Settings.BackgroundName ?? "None", reserve: hasImage ? 176 : 92);
        DrawButton(g, Find(Kind.Browse), "Browse…", accent: false);
        if (hasImage) DrawButton(g, Find(Kind.Remove), "Remove", accent: false);
    }

    int ConfirmRows => (_actions.Count + ConfirmCols - 1) / ConfirmCols;
    int TimeoutRowY => ConfirmCardY + 62 + ConfirmRows * (ConfirmItemH + 4) + 6;
    int ScreenTimeCardY => TimeoutRowY + 28 + 12 + 8;

    void DrawConfirmCard(Graphics g)
    {
        var card = R(Pad, ConfirmCardY, W - Pad * 2, TimeoutRowY + 28 + 12 - ConfirmCardY);
        FillRound(g, card, S(6), _p.Tile, _p.Border);
        DrawCardText(g, card, "Ask for confirmation", "Choose which buttons ask before they run", reserve: 0);

        foreach (var item in _hits.Where(h => h.Kind == Kind.Confirm))
        {
            var r = item.Rect;
            if (item == _hover || item == _pressed)
                FillRound(g, r, S(4), item == _pressed ? _p.TilePressed : _p.TileHover);

            // Windows 11 style checkbox
            var box = new Rectangle(r.X + S(6), r.Y + (r.Height - S(18)) / 2, S(18), S(18));
            if (Settings.ShouldConfirm(item.Action!))
            {
                FillRound(g, box, S(4), item == _hover ? _p.AccentHover : _p.Accent);
                DrawText(g, "", _check, box, _p.OnAccent);
            }
            else
            {
                FillRound(g, box, S(4), _p.Tile, _p.SubText);
            }
            DrawText(g, item.Action!, _body, new Rectangle(box.Right + S(8), r.Y, r.Right - box.Right - S(8), r.Height), _p.Text, TextFormatFlags.Left);
        }

        // Auto-confirm: segmented choice of seconds
        DrawText(g, "Auto-confirm after", _body, R(Pad + CardPad, TimeoutRowY, 140, 28), _p.Text, TextFormatFlags.Left);
        int current = Settings.AutoConfirm;
        foreach (var chip in _hits.Where(h => h.Kind == Kind.Timeout))
        {
            string label = chip.Value == 0 ? "Off" : $"{chip.Value}s";
            if (chip.Value == current)
            {
                FillRound(g, chip.Rect, S(4), chip == _hover ? _p.AccentHover : _p.Accent);
                DrawText(g, label, _small, chip.Rect, _p.OnAccent);
            }
            else
            {
                var fill = chip == _pressed ? _p.TilePressed : chip == _hover ? _p.TileHover : _p.Tile;
                FillRound(g, chip.Rect, S(4), fill, _p.Border);
                DrawText(g, label, _small, chip.Rect, _p.Text);
            }
        }
    }

    void DrawButton(Graphics g, Hit h, string text, bool accent)
    {
        bool active = h == _hover || h == _pressed;
        if (accent)
        {
            FillRound(g, h.Rect, S(4), active ? _p.AccentHover : _p.Accent);
            DrawText(g, text, _body, h.Rect, _p.OnAccent);
        }
        else
        {
            FillRound(g, h.Rect, S(4), h == _pressed ? _p.TilePressed : active ? _p.TileHover : _p.Tile, _p.Border);
            DrawText(g, text, _body, h.Rect, h == _pressed ? _p.SubText : _p.Text);
        }
    }
}
