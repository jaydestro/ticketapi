internal enum LoadGenControlAction
{
    None,
    Paused,
    Resumed,
    Reset,
    Stop
}

internal sealed class LoadGenRuntimeControls
{
    private static readonly TimeSpan?[] DurationPresets =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        null
    ];

    public LoadGenRuntimeControls(int concurrency, TimeSpan? duration)
    {
        Concurrency = concurrency;
        Duration = duration;
        HelpVisible = false;
    }

    public int Concurrency { get; private set; }

    public TimeSpan? Duration { get; private set; }

    public bool IsPaused { get; private set; }

    public static bool HelpVisible { get; private set; }

    public string LastAction { get; private set; } = "running";

    public LoadGenControlAction Handle(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Spacebar:
                IsPaused = !IsPaused;
                LastAction = IsPaused ? "paused" : "resumed";
                return IsPaused ? LoadGenControlAction.Paused : LoadGenControlAction.Resumed;
            case ConsoleKey.Add:
            case ConsoleKey.OemPlus:
                Concurrency = Math.Min(4_000, Concurrency + 1);
                LastAction = $"concurrency {Concurrency}";
                return LoadGenControlAction.None;
            case ConsoleKey.Subtract:
            case ConsoleKey.OemMinus:
                Concurrency = Math.Max(1, Concurrency - 1);
                LastAction = $"concurrency {Concurrency}";
                return LoadGenControlAction.None;
            case ConsoleKey.R:
                LastAction = "counters reset";
                return LoadGenControlAction.Reset;
            case ConsoleKey.T:
                Duration = GetNextDuration(Duration);
                LastAction = $"time {FormatDuration(Duration)}";
                return LoadGenControlAction.None;
            case ConsoleKey.H:
                HelpVisible = !HelpVisible;
                LastAction = HelpVisible ? "help opened" : "help closed";
                return LoadGenControlAction.None;
            case ConsoleKey.Q:
                LastAction = "stopping";
                return LoadGenControlAction.Stop;
            default:
                return LoadGenControlAction.None;
        }
    }

    public static string FormatDuration(TimeSpan? duration) => duration switch
    {
        null => "unlimited",
        { TotalSeconds: < 60 } value => $"{value.TotalSeconds:F0}s",
        { TotalMinutes: < 60 } value => $"{value.TotalMinutes:F0}m",
        var value => $"{value.Value.TotalHours:F0}h"
    };

    private static TimeSpan? GetNextDuration(TimeSpan? current)
    {
        var currentIndex = Array.FindIndex(
            DurationPresets,
            preset => Nullable.Equals(preset, current));
        if (currentIndex >= 0)
        {
            return DurationPresets[(currentIndex + 1) % DurationPresets.Length];
        }

        return DurationPresets.FirstOrDefault(preset => preset is not null && preset > current);
    }
}