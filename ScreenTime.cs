using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace PowerTray;

record AppUsage(string Name, string? Path, int Seconds);

/// <summary>
/// Adds up how long each app is in the foreground while someone is using the PC.
/// Kept on this PC only, one JSON file per day, for <see cref="KeepDays"/> days.
/// </summary>
sealed class ScreenTime : IDisposable
{
    public const int KeepDays = 90;
    static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(5);
    // A longer gap between samples means the PC slept or stalled, not that anyone used it
    static readonly TimeSpan MaxStep = TimeSpan.FromSeconds(15);
    const long SaveEveryMs = 60_000;

    sealed class Entry
    {
        public string Name { get; set; } = "";
        public string? Path { get; set; }
        public double Seconds { get; set; }
    }

    readonly System.Windows.Forms.Timer _poll = new() { Interval = 5000 };
    readonly Stopwatch _sinceSample = new();
    readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, Entry> _today = new(StringComparer.OrdinalIgnoreCase);
    DateOnly _day;
    bool _locked, _dirty;
    long _savedAt;

    /// <summary>After each sample that added time.</summary>
    public event Action? Updated;

    static string Dir => Path.Combine(Settings.DataDir, "ScreenTime");
    static string FileFor(DateOnly day) => Path.Combine(Dir, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json");
    static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    public ScreenTime()
    {
        _poll.Tick += (_, _) => Sample();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        Settings.Changed += Apply;
        Apply();
    }

    /// <summary>Starts or stops tracking to match the setting.</summary>
    void Apply()
    {
        if (Settings.ScreenTime == _poll.Enabled) return;
        if (Settings.ScreenTime)
        {
            _day = Today;
            _today = Load(_day);
            Prune();
            _sinceSample.Restart();
            _poll.Start();
        }
        else
        {
            Sample();
            _poll.Stop();
            Save();
        }
    }

    void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock) _locked = true;
        else if (e.Reason == SessionSwitchReason.SessionUnlock) _locked = false;
    }

    void Sample()
    {
        var step = _sinceSample.Elapsed;
        _sinceSample.Restart();

        if (Today != _day)
        {
            Save();
            _day = Today;
            _today = Load(_day);
            Prune();
        }
        if (step > MaxStep || _locked || IdleTime() >= IdleAfter) return;
        if (ForegroundApp() is not { } app) return;
        var (name, path) = app;

        string key = path ?? name;
        if (!_today.TryGetValue(key, out var entry))
            _today[key] = entry = new Entry { Name = name, Path = path };
        entry.Seconds += step.TotalSeconds;
        _dirty = true;
        if (Environment.TickCount64 - _savedAt >= SaveEveryMs) Save();
        Updated?.Invoke();
    }

    /// <summary>Apps used on a day, most used first.</summary>
    public IReadOnlyList<AppUsage> Day(DateOnly day)
    {
        var entries = day == _day && _poll.Enabled ? _today : Load(day);
        return entries.Values
            .Select(e => new AppUsage(e.Name, e.Path, (int)e.Seconds))
            .Where(u => u.Seconds > 0)
            .OrderByDescending(u => u.Seconds)
            .ToList();
    }

    /// <summary>The earliest day with saved data, or today when there is none.</summary>
    public static DateOnly FirstDay()
    {
        try
        {
            if (!Directory.Exists(Dir)) return Today;
            var days = Directory.GetFiles(Dir, "*.json").Select(ParseDay).OfType<DateOnly>().ToList();
            return days.Count > 0 ? days.Min() : Today;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Today;
        }
    }

    static DateOnly? ParseDay(string file) =>
        DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    static Dictionary<string, Entry> Load(DateOnly day)
    {
        try
        {
            string file = FileFor(day);
            if (File.Exists(file) && JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(file)) is { } saved)
                return new(saved, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    void Save()
    {
        _savedAt = Environment.TickCount64;
        if (!_dirty) return;
        try
        {
            Directory.CreateDirectory(Dir);
            // Write then swap, so a crash mid-write can't leave a half-written day
            string file = FileFor(_day), temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_today));
            File.Move(temp, file, overwrite: true);
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    static void Prune()
    {
        try
        {
            if (!Directory.Exists(Dir)) return;
            var oldest = Today.AddDays(-(KeepDays - 1));
            foreach (var file in Directory.GetFiles(Dir, "*.json"))
                if (ParseDay(file) is { } d && d < oldest) File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    static TimeSpan IdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info)
            ? TimeSpan.FromMilliseconds((uint)Environment.TickCount - info.dwTime)
            : TimeSpan.Zero;
    }

    (string Name, string? Path)? ForegroundApp()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out uint pid);
        string? path = ImagePath(pid);

        // Store apps draw inside a frame host window; the app itself owns a child window
        if (path != null && Path.GetFileName(path).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            EnumChildWindows(hwnd, (child, _) =>
            {
                GetWindowThreadProcessId(child, out uint childPid);
                if (childPid == pid) return true;
                path = ImagePath(childPid) ?? path;
                return false;
            }, IntPtr.Zero);
        }

        if (path == null)
        {
            try { return (Process.GetProcessById((int)pid).ProcessName, null); }
            catch (ArgumentException) { return null; }
        }
        return (DisplayName(path), path);
    }

    /// <summary>The exe's description ("Google Chrome"), falling back to its file name.</summary>
    string DisplayName(string path)
    {
        if (_names.TryGetValue(path, out var name)) return name;
        try { name = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim(); }
        catch (FileNotFoundException) { }
        if (string.IsNullOrEmpty(name)) name = Path.GetFileNameWithoutExtension(path);
        return _names[path] = name;
    }

    static string? ImagePath(uint pid)
    {
        var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(handle); }
    }

    /// <summary>"2h 5m", "52m" or "&lt;1m".</summary>
    public static string Format(int seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m"
            : t.TotalMinutes >= 1 ? $"{t.Minutes}m"
            : "<1m";
    }

    public void Dispose()
    {
        Settings.Changed -= Apply;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        if (_poll.Enabled)
        {
            Sample();
            Save();
        }
        _poll.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);
}
