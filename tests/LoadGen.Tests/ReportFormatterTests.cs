namespace LoadGen.Tests;

public sealed class ReportFormatterTests
{
    [Fact]
    public void Compact_interval_keeps_every_line_within_80_columns()
    {
        var metrics = new MetricsCollector();
        var previous = metrics.Snapshot();
        metrics.RecordSent(RequestKind.OrdersByEvent);
        metrics.RecordCompleted(
            RequestKind.OrdersByEvent,
            200,
            65_184.407056752584,
            TimeSpan.FromMilliseconds(2_429));

        var lines = LoadGenReportFormatter.FormatInterval(
            new string('b', 40),
            LoadProfiles.Comparison,
            false,
            50,
            TimeSpan.FromSeconds(76),
            TimeSpan.FromSeconds(2),
            previous,
            metrics.Snapshot(),
            80,
            new DateTimeOffset(2026, 8, 11, 15, 0, 20, TimeSpan.Zero));

        Assert.All(lines, line => Assert.True(line.Length <= 80, $"Line was {line.Length} columns: {line}"));
        Assert.Contains("run=" + new string('b', 40), lines[0]);
        Assert.Contains(lines, line => line.Contains("total    65184.4", StringComparison.Ordinal));
        Assert.Equal(13, lines.Count);
        Assert.DoesNotContain(lines, line => line.Contains("purchase write", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("create event", StringComparison.Ordinal));
    }

    [Fact]
    public void Wide_interval_uses_one_row_per_operation_and_shows_before_label()
    {
        var metrics = new MetricsCollector();
        var lines = LoadGenReportFormatter.FormatInterval(
            "before",
            LoadProfiles.Comparison,
            false,
            10,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2),
            metrics.Snapshot(),
            metrics.Snapshot(),
            160,
            DateTimeOffset.UnixEpoch);

        Assert.Equal(7, lines.Count);
        Assert.Contains("run=before", lines[0]);
        Assert.Contains("total RU", lines[1]);
    }

    [Fact]
    public void Final_report_is_attributable_to_after_run()
    {
        var metrics = new MetricsCollector();
        metrics.RecordSent(RequestKind.EventDetail);
        metrics.RecordCompleted(RequestKind.EventDetail, 200, 1, TimeSpan.FromMilliseconds(5));

        var lines = LoadGenReportFormatter.FormatFinal(
            "after",
            LoadProfiles.Comparison,
            TimeSpan.FromSeconds(60),
            metrics.Snapshot());

        Assert.Contains("run=after", lines[1]);
        Assert.Contains("profile=comparison", lines[1]);
        Assert.Contains(lines, line => line.StartsWith("TOTAL", StringComparison.Ordinal));
    }

    [Fact]
    public void Network_failure_has_finite_p95_and_missing_ru_is_explained()
    {
        var metrics = new MetricsCollector();
        var previous = metrics.Snapshot();
        metrics.RecordSent(RequestKind.OrdersByEvent);
        metrics.RecordNetworkFailure(RequestKind.OrdersByEvent, TimeSpan.FromMilliseconds(30_014));

        var lines = LoadGenReportFormatter.FormatInterval(
            "before",
            LoadProfiles.Comparison,
            false,
            50,
            TimeSpan.FromSeconds(66),
            TimeSpan.FromSeconds(2),
            previous,
            metrics.Snapshot(),
            160,
            DateTimeOffset.UnixEpoch);

        var row = Assert.Single(
            lines,
            line => line.StartsWith("hot-event orders", StringComparison.Ordinal));
        Assert.Contains("no rsp", row);
        Assert.DoesNotContain("179769", row);
        Assert.DoesNotContain(">=600s", row);
    }
}