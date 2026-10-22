using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace PowerTray;

/// <summary>Drawing and window helpers shared by the popup and the settings dialog.</summary>
static class Ui
{
    // Segoe UI Variable and Fluent Icons ship with Windows 11; fall back for Windows 10
    public static string TextFont { get; } = FontExists("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
    public static string IconFont { get; } = FontExists("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    /// <summary>
    /// Set while drawing into an off-screen bitmap: GDI text loses anti-aliasing there,
    /// so DrawText switches to GDI+.
    /// </summary>
    [ThreadStatic] public static bool Offscreen;

    public static void DrawText(Graphics g, string text, Font font, Rectangle r, Color color,
        TextFormatFlags align = TextFormatFlags.HorizontalCenter)
    {
        if (!Offscreen)
        {
            TextRenderer.DrawText(g, text, font, r, color,
                align | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            return;
        }

        using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.Alignment = align.HasFlag(TextFormatFlags.HorizontalCenter) ? StringAlignment.Center
            : align.HasFlag(TextFormatFlags.Right) ? StringAlignment.Far : StringAlignment.Near;
        format.LineAlignment = StringAlignment.Center;
        format.Trimming = StringTrimming.EllipsisCharacter;
        format.FormatFlags |= StringFormatFlags.NoWrap;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var brush = new SolidBrush(color);
        g.DrawString(text, font, brush, r, format);
    }

    public static GraphicsPath RoundPath(Rectangle r, int radius)
    {
        r = new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1);
        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        if (d < 1)
        {
            // Too small for arcs (e.g. a progress bar near zero); GDI+ rejects zero-size arcs
            if (r.Width > 0 && r.Height > 0) path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRound(Graphics g, Rectangle r, int radius, Color fill, Color? border = null)
    {
        using var path = RoundPath(r, radius);
        using (var b = new SolidBrush(fill)) g.FillPath(b, path);
        if (border is Color c)
            using (var pen = new Pen(c)) g.DrawPath(pen, path);
    }

    static bool FontExists(string name)
    {
        using var f = new Font(name, 10);
        return f.Name == name;
    }

    public static int MonitorDpi(Point pt)
    {
        var monitor = MonitorFromPoint(pt, 2 /* MONITOR_DEFAULTTONEAREST */);
        return GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? (int)dpi : 96;
    }

    /// <summary>Dark/light frame, optional rounded corners and caption color.</summary>
    public static void ApplyWindowTheme(IntPtr hwnd, Palette p, bool round = false, Color? caption = null)
    {
        int dark = p.Dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        if (round)
        {
            int pref = 2;
            DwmSetWindowAttribute(hwnd, 33, ref pref, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE = ROUND
        }
        if (caption is Color c)
        {
            int colorRef = c.R | (c.G << 8) | (c.B << 16);
            DwmSetWindowAttribute(hwnd, 35, ref colorRef, sizeof(int)); // DWMWA_CAPTION_COLOR
        }
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point pt, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}
