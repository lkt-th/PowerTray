namespace PowerTray;

/// <summary>Counts down to a deadline and fires once, with a one-minute warning.</summary>
sealed class ShutdownTimer : IDisposable
{
    readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };
    bool _warned;

    public DateTime? Deadline { get; private set; }
    public bool Active => Deadline != null;
    public TimeSpan Remaining => Deadline is { } d && d > DateTime.Now ? d - DateTime.Now : TimeSpan.Zero;

    /// <summary>Started or cancelled.</summary>
    public event Action? Changed;
    /// <summary>Every second while running.</summary>
    public event Action? Tick;
    public event Action? Warning;
    public event Action? Elapsed;

    public ShutdownTimer() => _tick.Tick += OnTick;

    public void Start(TimeSpan delay)
    {
        Deadline = DateTime.Now + delay;
        _warned = delay <= TimeSpan.FromMinutes(1);
        _tick.Start();
        Changed?.Invoke();
    }

    public void Cancel()
    {
        if (!Active) return;
        _tick.Stop();
        Deadline = null;
        Changed?.Invoke();
    }

    void OnTick(object? sender, EventArgs e)
    {
        var left = Remaining;
        if (left == TimeSpan.Zero)
        {
            Cancel();
            Elapsed?.Invoke();
            return;
        }
        if (!_warned && left <= TimeSpan.FromMinutes(1))
        {
            _warned = true;
            Warning?.Invoke();
        }
        Tick?.Invoke();
    }

    public static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    public static string FormatMinutes(int minutes) =>
        minutes < 60 ? $"{minutes} min"
        : minutes % 60 == 0 ? $"{minutes / 60} hr"
        : $"{minutes / 60} hr {minutes % 60} min";

    public void Dispose() => _tick.Dispose();
}
