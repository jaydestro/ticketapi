namespace LoadGen.Tests;

public sealed class SaturationControllerTests
{
    [Fact]
    public void Enabled_controller_doubles_until_throttling_then_holds()
    {
        var controller = new LoadGenSaturationController(true, 10);

        controller.ObserveAndAdvance(0, isPaused: false);
        Assert.Equal(20, controller.TargetConcurrency);

        controller.ObserveAndAdvance(1, isPaused: false);
        Assert.True(controller.ThrottlingObserved);
        Assert.Equal(20, controller.TargetConcurrency);

        controller.ObserveAndAdvance(0, isPaused: false);
        Assert.Equal(20, controller.TargetConcurrency);
    }

    [Fact]
    public void Paused_or_disabled_controller_does_not_ramp()
    {
        var paused = new LoadGenSaturationController(true, 10);
        paused.ObserveAndAdvance(0, isPaused: true);
        Assert.Equal(10, paused.TargetConcurrency);

        var disabled = new LoadGenSaturationController(false, 10);
        disabled.ObserveAndAdvance(0, isPaused: false);
        Assert.Equal(10, disabled.TargetConcurrency);
    }

    [Fact]
    public void Controller_caps_target_at_maximum_concurrency()
    {
        var controller = new LoadGenSaturationController(true, 3_000);

        controller.ObserveAndAdvance(0, isPaused: false);
        controller.ObserveAndAdvance(0, isPaused: false);

        Assert.Equal(LoadGenConstants.MaximumConcurrency, controller.TargetConcurrency);
    }

    [Fact]
    public void Immediate_throttle_signal_remains_latched_without_metric_count()
    {
        var controller = new LoadGenSaturationController(true, 10);

        controller.ObserveThrottling();
        controller.ObserveAndAdvance(0, isPaused: false);

        Assert.True(controller.ThrottlingObserved);
        Assert.Equal(10, controller.TargetConcurrency);
    }
}
