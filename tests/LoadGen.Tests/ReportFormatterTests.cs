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
            CosmosQueryScope.CrossPartition,
            TimeSpan.FromMilliseconds(2_429));

        var lines = LoadGenLiveDashboardFormatter.Format(
            LoadProfiles.Comparison,
            false,
            50,
            TimeSpan.FromSeconds(76),
            new DashboardRenderContext(false, null, false),
            metrics.Snapshot(),
            80,
            new DateTimeOffset(2026, 8, 11, 15, 0, 20, TimeSpan.Zero));

        Assert.All(lines, line => Assert.True(line.Length <= 80, $"Line was {line.Length} columns: {line}"));
        Assert.Contains("READ", lines[0]);
        Assert.Contains("total RU 65184.4", lines[1]);
        Assert.Contains(lines, line => line.Contains("65184.41", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("XPK", StringComparison.Ordinal));
        Assert.Equal(8, lines.Count);
        Assert.DoesNotContain(lines, line => line.Contains("purchase write", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("create event", StringComparison.Ordinal));
    }

    [Fact]
    public void Wide_interval_uses_one_row_per_operation_and_shows_controls()
    {
        var metrics = new MetricsCollector();
        var lines = LoadGenLiveDashboardFormatter.Format(
            LoadProfiles.Comparison,
            false,
            10,
            TimeSpan.FromSeconds(2),
            new DashboardRenderContext(false, null, true),
            metrics.Snapshot(),
            160,
            DateTimeOffset.UnixEpoch);

        Assert.Equal(8, lines.Count);
        Assert.Contains("workload=read", lines[0]);
        Assert.Contains("H help", lines[1]);
        Assert.Contains("Q quit", lines[1]);
        Assert.Contains("RU/query", lines[2]);
    }

    [Fact]
    public void Compact_interval_shows_active_requests_and_429s_cumulatively()
    {
        var metrics = new MetricsCollector();
        metrics.RecordSent(RequestKind.OrdersByEvent);
        metrics.RecordSent(RequestKind.UpcomingEvents);
        metrics.RecordCompleted(RequestKind.UpcomingEvents, 429, 2.75, TimeSpan.FromMilliseconds(30));

        var lines = LoadGenLiveDashboardFormatter.Format(
            LoadProfiles.Comparison,
            false,
            30,
            TimeSpan.FromSeconds(1),
            new DashboardRenderContext(false, null, false),
            metrics.Snapshot(),
            80,
            DateTimeOffset.UnixEpoch);

        Assert.Contains("active 1", lines[1]);
        Assert.Contains("429 1", lines[1]);
        var upcoming = Assert.Single(
            lines,
            line => line.StartsWith("upcoming query", StringComparison.Ordinal));
        Assert.Matches(@"upcoming query\s+\?\s+0\s+1\s+0\s+1\s+0\s+2\.75", upcoming);
        var hotEvent = Assert.Single(
            lines,
            line => line.StartsWith("hot-event orders", StringComparison.Ordinal));
        Assert.Matches(@"hot-event orders\s+\?\s+1\s+1\s+0\s+0\s+0\s+-\s+-", hotEvent);
    }

    [Fact]
    public void Final_report_identifies_workload()
    {
        var metrics = new MetricsCollector();
        metrics.RecordSent(RequestKind.EventDetail);
        metrics.RecordCompleted(RequestKind.EventDetail, 200, 1, TimeSpan.FromMilliseconds(5));

        var lines = LoadGenLiveDashboardFormatter.FormatFinal(
            LoadProfiles.Comparison,
            TimeSpan.FromSeconds(60),
            metrics.Snapshot());

        Assert.Contains("read", lines[1]);
        Assert.Contains(lines, line => line.StartsWith("Total used RU:", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("TOTAL", StringComparison.Ordinal));
    }

    [Fact]
    public void Compact_final_report_fits_80_columns_and_shows_ru_and_429()
    {
        var metrics = new MetricsCollector();
        metrics.RecordSent(RequestKind.UpcomingEvents);
        metrics.RecordCompleted(RequestKind.UpcomingEvents, 429, 3.25, TimeSpan.FromMilliseconds(40));

        var lines = LoadGenLiveDashboardFormatter.FormatFinal(
            LoadProfiles.Comparison,
            TimeSpan.FromSeconds(60),
            metrics.Snapshot(),
            80);

        Assert.All(lines, line => Assert.True(line.Length <= 80, $"Line was {line.Length} columns: {line}"));
        Assert.Contains(lines, line => line.StartsWith("Total used RU:", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("RU/q", StringComparison.Ordinal));
        var upcoming = Assert.Single(
            lines,
            line => line.StartsWith("upcoming query", StringComparison.Ordinal));
        Assert.Matches(@"upcoming query\s+\?\s+1\s+0\s+1\s+0\s+3\.25", upcoming);
    }

    [Fact]
    public void Network_failure_has_finite_p95_and_missing_ru_is_explained()
    {
        var metrics = new MetricsCollector();
        var previous = metrics.Snapshot();
        metrics.RecordSent(RequestKind.OrdersByEvent);
        metrics.RecordNetworkFailure(RequestKind.OrdersByEvent, TimeSpan.FromMilliseconds(30_014));

        var lines = LoadGenLiveDashboardFormatter.Format(
            LoadProfiles.Comparison,
            false,
            50,
            TimeSpan.FromSeconds(66),
            new DashboardRenderContext(false, null, false),
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

    [Fact]
    public void Help_explains_all_headers_scopes_and_operations_within_80_columns()
    {
        var lines = LoadGenLiveDashboardFormatter.FormatHelp(80);

        Assert.All(lines, line => Assert.True(line.Length <= 80, $"Line was {line.Length} columns: {line}"));
        foreach (var header in new[] { "operation", "scope", "act", "sent", "ok", "429", "err", "RU/q", "p95", "done", "2xx", "4xx", "5xx", "net", "avg ms", "total RU" })
        {
            Assert.Contains(lines, line => line.Contains(header, StringComparison.Ordinal));
        }

        foreach (var scope in new[] { "XPK", "1PK", "POINT", "N/A", "MIXED", "?" })
        {
            Assert.Contains(lines, line => line.Contains(scope, StringComparison.Ordinal));
        }

        foreach (var operation in new[] { "event point read", "upcoming query", "city query", "purchase write", "customer orders", "hot-event orders", "create event" })
        {
            Assert.Contains(lines, line => line.Contains(operation, StringComparison.Ordinal));
        }
    }
}