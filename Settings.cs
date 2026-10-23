using Microsoft.Win32;

namespace PowerTray;

/// <summary>User settings stored under HKCU.</summary>
static class Settings
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string AppKey = @"Software\PowerTray";
    const string AppName = "PowerTray";
    public const int MaxTransparency = 50;

    public static event Action? Changed;

    public static bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(AppName) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(AppName, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(AppName, false);
            Changed?.Invoke();
        }
    }

    /// <summary>How see-through the power menu is, 0 to MaxTransparency percent.</summary>
    public static int Transparency
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(AppKey);
            return key?.GetValue("Transparency") is int v ? Math.Clamp(v, 0, MaxTransparency) : 0;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(AppKey);
            key.SetValue("Transparency", Math.Clamp(value, 0, MaxTransparency), RegistryValueKind.DWord);
            Changed?.Invoke();
        }
    }

    public static double Opacity => 1 - Transparency / 100.0;

    public static readonly int[] AutoConfirmChoices = { 0, 5, 10, 15, 30 };

    /// <summary>
    /// Seconds before an unanswered confirmation runs the action by itself; 0 (the default) means never.
    /// </summary>
    public static int AutoConfirm
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(AppKey);
            return key?.GetValue("AutoConfirm") is int v && v >= 0 ? v : 0;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(AppKey);
            key.SetValue("AutoConfirm", Math.Max(0, value), RegistryValueKind.DWord);
            Changed?.Invoke();
        }
    }

    /// <summary>Whether to record how long each app is used. Off by default.</summary>
    public static bool ScreenTime
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(AppKey);
            return key?.GetValue("ScreenTime") is int v && v != 0;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(AppKey);
            key.SetValue("ScreenTime", value ? 1 : 0, RegistryValueKind.DWord);
            Changed?.Invoke();
        }
    }

    /// <summary>Whether a power button asks before running. All do by default.</summary>
    public static bool ShouldConfirm(string action) => !NoConfirm().Contains(action);

    public static void SetConfirm(string action, bool confirm)
    {
        var skip = NoConfirm();
        if (confirm) skip.Remove(action);
        else skip.Add(action);
        using var key = Registry.CurrentUser.CreateSubKey(AppKey);
        key.SetValue("NoConfirm", string.Join(",", skip));
        Changed?.Invoke();
    }

    // Stores the buttons that skip confirmation, so any new button defaults to asking
    static HashSet<string> NoConfirm() =>
        (GetString("NoConfirm") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerTray");

    /// <summary>PowerTray's own copy of the background image, or null when none is set.</summary>
    public static string? BackgroundPath =>
        GetString("Background") is { } path && File.Exists(path) ? path : null;

    /// <summary>File name of the image the user picked, for display.</summary>
    public static string? BackgroundName => BackgroundPath != null ? GetString("BackgroundName") : null;

    /// <summary>Copies the image into app data so it survives the original being moved or deleted.</summary>
    public static void SetBackground(string source)
    {
        Directory.CreateDirectory(DataDir);
        string dest = Path.Combine(DataDir, "background" + Path.GetExtension(source).ToLowerInvariant());
        string? old = BackgroundPath;
        if (!string.Equals(Path.GetFullPath(source), dest, StringComparison.OrdinalIgnoreCase))
            File.Copy(source, dest, overwrite: true);
        if (old != null && !string.Equals(old, dest, StringComparison.OrdinalIgnoreCase))
            File.Delete(old);

        using var key = Registry.CurrentUser.CreateSubKey(AppKey);
        key.SetValue("Background", dest);
        key.SetValue("BackgroundName", Path.GetFileName(source));
        Changed?.Invoke();
    }

    public static void ClearBackground()
    {
        if (BackgroundPath is { } path) File.Delete(path);
        using var key = Registry.CurrentUser.CreateSubKey(AppKey);
        key.DeleteValue("Background", false);
        key.DeleteValue("BackgroundName", false);
        Changed?.Invoke();
    }

    /// <summary>Loads an image without keeping the file locked.</summary>
    public static Image LoadImage(string path) => Image.FromStream(new MemoryStream(File.ReadAllBytes(path)));

    static string? GetString(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(AppKey);
        return key?.GetValue(name) as string;
    }
}
