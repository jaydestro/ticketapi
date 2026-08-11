namespace LoadGen.Tests;

public sealed class MetricsCollectorTests
{
    [Fact]
    public void Snapshot_tracks_statuses_charges_latency_and_totals()
    {
        var metrics = new MetricsCollector();

        metrics.RecordSent(RequestKind.EventDetail);
        metrics.RecordCompleted(RequestKind.EventDetail, 200, 2.5, TimeSpan.FromMilliseconds(6));
        metrics.RecordSent(RequestKind.EventsByCity);
        metrics.RecordCompleted(RequestKind.EventsByCity, 404, null, TimeSpan.FromMilliseconds(60));
        metrics.RecordSent(RequestKind.OrdersByEvent);
        metrics.RecordCompleted(RequestKind.OrdersByEvent, 503, 8.25, TimeSpan.FromMilliseconds(600));
        metrics.RecordSent(RequestKind.OrdersByCustomer);
        metrics.RecordNetworkFailure(RequestKind.OrdersByCustomer, TimeSpan.FromMilliseconds(20));

        var snapshot = metrics.Snapshot();

        Assert.Equal(1, snapshot[RequestKind.EventDetail].Success);
        Assert.Equal(1, snapshot[RequestKind.EventsByCity].ClientErrors);
        Assert.Equal(1, snapshot[RequestKind.OrdersByEvent].ServerErrors);
        Assert.Equal(1, snapshot[RequestKind.OrdersByCustomer].NetworkErrors);
        Assert.Equal(2.5, snapshot[RequestKind.EventDetail].RequestCharge);
        Assert.Equal(6, MetricsCollector.GetPercentileMilliseconds(
            snapshot[RequestKind.EventDetail].Histogram, 0.95));
        Assert.Equal(4, snapshot.Total.Sent);
        Assert.Equal(4, snapshot.Total.Completed);
        Assert.Equal(10.75, snapshot.Total.RequestCharge);
    }

    [Fact]
    public void Snapshot_subtraction_returns_interval_delta()
    {
        var metrics = new MetricsCollector();
        metrics.RecordSent(RequestKind.UpcomingEvents);
        metrics.RecordCompleted(RequestKind.UpcomingEvents, 200, 3, TimeSpan.FromMilliseconds(25));
        var previous = metrics.Snapshot();

        metrics.RecordSent(RequestKind.UpcomingEvents);
        metrics.RecordCompleted(RequestKind.UpcomingEvents, 200, 4, TimeSpan.FromMilliseconds(50));
        var delta = metrics.Snapshot()[RequestKind.UpcomingEvents] - previous[RequestKind.UpcomingEvents];

        Assert.Equal(1, delta.Sent);
        Assert.Equal(1, delta.Success);
        Assert.Equal(4, delta.RequestCharge);
        Assert.Equal(50, delta.TotalMilliseconds);
        Assert.Equal(1, delta.Histogram.Sum());
    }

    [Fact]
    public void Concurrent_updates_are_not_lost()
    {
        var metrics = new MetricsCollector();

        Parallel.For(0, 2_000, _ =>
        {
            metrics.RecordSent(RequestKind.EventDetail);
            metrics.RecordCompleted(RequestKind.EventDetail, 200, 1, TimeSpan.FromMilliseconds(1));
        });

        var value = metrics.Snapshot()[RequestKind.EventDetail];
        Assert.Equal(2_000, value.Sent);
        Assert.Equal(2_000, value.Completed);
        Assert.Equal(2_000, value.Success);
        Assert.Equal(2_000, value.Charged);
        Assert.Equal(2_000, value.RequestCharge);
    }

    [Fact]
    public void Empty_histogram_has_zero_percentile()
    {
        var metrics = new MetricsCollector();
        Assert.Equal(0, MetricsCollector.GetPercentileMilliseconds(
            metrics.Snapshot()[RequestKind.EventDetail].Histogram,
            0.95));
    }

    [Fact]
    public void Percentile_bucket_is_within_five_percent_of_long_latency()
    {
        var metrics = new MetricsCollector();
        metrics.RecordSent(RequestKind.OrdersByEvent);
        metrics.RecordCompleted(
            RequestKind.OrdersByEvent,
            200,
            1,
            TimeSpan.FromMilliseconds(8_200));

        var p95 = MetricsCollector.GetPercentileMilliseconds(
            metrics.Snapshot()[RequestKind.OrdersByEvent].Histogram,
            0.95);

        Assert.InRange(p95, 8_200, 8_610);
    }
}