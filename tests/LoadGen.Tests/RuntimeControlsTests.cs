namespace LoadGen.Tests;

public sealed class RuntimeControlsTests
{
    [Fact]
    public void Space_toggles_pause_and_resume()
    {
        var controls = new LoadGenRuntimeControls(10, null);

        Assert.Equal(LoadGenControlAction.Paused, controls.Handle(Key(ConsoleKey.Spacebar, ' ')));
        Assert.True(controls.IsPaused);
        Assert.Equal(LoadGenControlAction.Resumed, controls.Handle(Key(ConsoleKey.Spacebar, ' ')));
        Assert.False(controls.IsPaused);
    }

    [Fact]
    public void Plus_and_minus_adjust_concurrency_within_bounds()
    {
        var controls = new LoadGenRuntimeControls(1, null);

        controls.Handle(Key(ConsoleKey.OemMinus, '-'));
        Assert.Equal(1, controls.Concurrency);
        controls.Handle(Key(ConsoleKey.OemPlus, '+'));
        Assert.Equal(2, controls.Concurrency);
    }

    [Fact]
    public void Time_cycles_presets_and_reset_and_quit_return_actions()
    {
        var controls = new LoadGenRuntimeControls(10, null);

        controls.Handle(Key(ConsoleKey.T, 't'));
        Assert.Equal(TimeSpan.FromSeconds(30), controls.Duration);
        Assert.Equal("30s", LoadGenRuntimeControls.FormatDuration(controls.Duration));
        Assert.Equal(LoadGenControlAction.Reset, controls.Handle(Key(ConsoleKey.R, 'r')));
        Assert.Equal(LoadGenControlAction.Stop, controls.Handle(Key(ConsoleKey.Q, 'q')));
    }

    [Fact]
    public void H_toggles_dashboard_help()
    {
        var controls = new LoadGenRuntimeControls(10, null);

        controls.Handle(Key(ConsoleKey.H, 'h'));
        Assert.True(LoadGenRuntimeControls.HelpVisible);
        Assert.Equal("help opened", controls.LastAction);

        controls.Handle(Key(ConsoleKey.H, 'h'));
        Assert.False(LoadGenRuntimeControls.HelpVisible);
        Assert.Equal("help closed", controls.LastAction);
    }

    private static ConsoleKeyInfo Key(ConsoleKey key, char value) => new(value, key, false, false, false);
}