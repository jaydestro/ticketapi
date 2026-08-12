internal sealed class LoadGenSaturationController
{
    private int _throttlingObserved;

    public LoadGenSaturationController(bool enabled, int initialConcurrency)
    {
        Enabled = enabled;
        TargetConcurrency = initialConcurrency;
    }

    public bool Enabled { get; }

    public int TargetConcurrency { get; private set; }

    public bool ThrottlingObserved => Volatile.Read(ref _throttlingObserved) == 1;

    public bool IsAtMaximum => TargetConcurrency == LoadGenConstants.MaximumConcurrency;

    public void ObserveAndAdvance(long throttledResponses, bool isPaused)
    {
        if (!Enabled)
        {
            return;
        }

        if (throttledResponses > 0)
        {
            ObserveThrottling();
        }

        if (!isPaused && !ThrottlingObserved)
        {
            TargetConcurrency = Math.Min(LoadGenConstants.MaximumConcurrency, TargetConcurrency * 2);
        }
    }

    public void SetTarget(int concurrency) =>
        TargetConcurrency = Math.Clamp(concurrency, 1, LoadGenConstants.MaximumConcurrency);

    public void ObserveThrottling() => Interlocked.Exchange(ref _throttlingObserved, 1);
}
