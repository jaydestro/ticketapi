internal static class LoadGenReportFormatter
{
    private const int WideLayoutMinimumWidth = 120;

    public static IReadOnlyList<string> FormatInterval(
        string runLabel,
        string profile,
        bool inBurst,
        int targetConcurrency,
        TimeSpan elapsed,
        TimeSpan interval,
        MetricsSnapshot previous,
        MetricsSnapshot current,
        int width,
        DateTimeOffset reportTime)
    {
        var compact = width < WideLayoutMinimumWidth;
        var lines = new List<string>();
        if (compact)
        {
            lines.Add($"[{reportTime:HH:mm:ss}] run={runLabel}");
            lines.Add(
                $"profile={profile} mode={(inBurst ? "burst" : "steady")} " +
                $"concurrency={targetConcurrency} elapsed={elapsed:hh\\:mm\\:ss}");
        }
        else
        {
            lines.Add(
                $"[{reportTime:HH:mm:ss}] run={runLabel} profile={profile} " +
                $"mode={(inBurst ? "burst" : "steady")} concurrency={targetConcurrency} " +
                $"elapsed={elapsed:hh\\:mm\\:ss}");
        }

        lines.Add(compact
            ? $"{"operation",-20} {"req/s",6} {"ok",6} {"4xx",6} {"5xx",6} {"net",6}"
            : $"{"operation",-22} {"req/s",7} {"2xx",7} {"4xx",6} {"5xx",6} {"net",5} " +
              $"{"avg ms",8} {"p95 ms",8} {"avg RU",8} {"RU/s",9} {"total RU",11}");

        foreach (var kind in LoadGenProfiles.GetRequestKinds(profile))
        {
            var delta = current[kind] - previous[kind];
            var seconds = Math.Max(interval.TotalSeconds, 0.001);
            var averageMilliseconds = delta.Completed > 0 ? delta.TotalMilliseconds / delta.Completed : 0;
            var averageRu = FormatRu(delta.Charged, delta.Completed, delta.RequestCharge, divideBy: delta.Charged);
            var ruPerSecond = FormatRu(delta.Charged, delta.Completed, delta.RequestCharge, divideBy: seconds);
            var totalRu = current[kind].Charged > 0 ? $"{current[kind].RequestCharge:F1}" : "-";
            var p95 = FormatLatency(MetricsCollector.GetPercentileMilliseconds(delta.Histogram, 0.95));

            if (compact)
            {
                lines.Add(
                    $"{LoadGenNames.GetDisplayName(kind),-20} {delta.Completed / seconds,6:F1} " +
                    $"{delta.Success,6:N0} {delta.ClientErrors,6:N0} {delta.ServerErrors,6:N0} " +
                    $"{delta.NetworkErrors,6:N0}");
                lines.Add(
                    $"  avg {averageMilliseconds,6:F0}ms p95 {p95,6}ms avgRU {averageRu,7} " +
                    $"RU/s {ruPerSecond,8} total {totalRu,10}");
            }
            else
            {
                lines.Add(
                    $"{LoadGenNames.GetDisplayName(kind),-22} {delta.Completed / seconds,7:F1} " +
                    $"{delta.Success,7:N0} {delta.ClientErrors,6:N0} {delta.ServerErrors,6:N0} " +
                    $"{delta.NetworkErrors,5:N0} {averageMilliseconds,8:F0} {p95,8} " +
                    $"{averageRu,8} {ruPerSecond,9} {totalRu,11}");
            }
        }

        return lines;
    }

    public static IReadOnlyList<string> FormatFinal(
        string runLabel,
        string profile,
        TimeSpan elapsed,
        MetricsSnapshot snapshot)
    {
        var lines = new List<string>
        {
            string.Empty,
            $"=== LoadGen final: run={runLabel} profile={profile} ({elapsed:hh\\:mm\\:ss}) ===",
            $"{"operation",-22} {"sent",9} {"done",9} {"2xx",9} {"4xx",7} {"5xx",7} {"net",7} " +
            $"{"avg ms",9} {"p95 ms",9} {"avg RU",9} {"total RU",12}"
        };

        foreach (var kind in LoadGenProfiles.GetRequestKinds(profile))
        {
            var value = snapshot[kind];
            var averageMilliseconds = value.Completed > 0 ? value.TotalMilliseconds / value.Completed : 0;
            var averageRu = FormatRu(value.Charged, value.Completed, value.RequestCharge, divideBy: value.Charged);
            var totalRu = value.Charged > 0 ? $"{value.RequestCharge:F1}" : "-";
            var p95 = FormatLatency(MetricsCollector.GetPercentileMilliseconds(value.Histogram, 0.95));
            lines.Add(
                $"{LoadGenNames.GetDisplayName(kind),-22} {value.Sent,9:N0} {value.Completed,9:N0} " +
                $"{value.Success,9:N0} {value.ClientErrors,7:N0} {value.ServerErrors,7:N0} " +
                $"{value.NetworkErrors,7:N0} {averageMilliseconds,9:F0} {p95,9} " +
                $"{averageRu,9} {totalRu,12}");
        }

        var totals = snapshot.Total;
        var averageTotalRu = FormatRu(
            totals.Charged,
            totals.Completed,
            totals.RequestCharge,
            divideBy: totals.Charged);
        var totalRequestCharge = totals.Charged > 0 ? $"{totals.RequestCharge:F1}" : "-";
        var totalP95 = FormatLatency(MetricsCollector.GetPercentileMilliseconds(totals.Histogram, 0.95));
        lines.Add(
            $"TOTAL                  {totals.Sent,9:N0} {totals.Completed,9:N0} " +
            $"{totals.Success,9:N0} {totals.ClientErrors,7:N0} {totals.ServerErrors,7:N0} " +
            $"{totals.NetworkErrors,7:N0} {totals.AverageMilliseconds,9:F0} " +
            $"{totalP95,9} " +
            $"{averageTotalRu,9} {totalRequestCharge,12}");

        return lines;
    }

    private static string FormatLatency(double milliseconds) =>
        double.IsPositiveInfinity(milliseconds) ? ">=600s" : $"{milliseconds:F0}";

    private static string FormatRu(long charged, long completed, double requestCharge, double divideBy) =>
        charged > 0 ? $"{requestCharge / divideBy:F2}" : completed > 0 ? "no rsp" : "-";
}

internal static class LoadGenNames
{
    public static string GetDisplayName(RequestKind kind) => kind switch
    {
        RequestKind.EventDetail => "event point read",
        RequestKind.UpcomingEvents => "upcoming query",
        RequestKind.EventsByCity => "city query",
        RequestKind.PurchaseTicket => "purchase write",
        RequestKind.OrdersByCustomer => "customer orders",
        RequestKind.OrdersByEvent => "hot-event orders",
        RequestKind.CreateEvent => "create event",
        _ => kind.ToString()
    };
}

internal static class LoadGenRoutes
{
    public static RequestKind? MatchKind(string method, string[] segments)
    {
        static bool IsParameter(string segment) => segment.StartsWith('{') && segment.EndsWith('}');
        var shape = segments.Select(segment => IsParameter(segment) ? "{}" : segment.ToLowerInvariant()).ToArray();
        var verb = method.ToUpperInvariant();

        return (verb, shape) switch
        {
            ("GET", ["api", "events", "{}"]) => RequestKind.EventDetail,
            ("GET", ["api", "events", "upcoming"]) => RequestKind.UpcomingEvents,
            ("GET", ["api", "events", "city", "{}"]) => RequestKind.EventsByCity,
            ("POST", ["api", "events"]) => RequestKind.CreateEvent,
            ("POST", ["api", "orders"]) => RequestKind.PurchaseTicket,
            ("GET", ["api", "orders", "customer", "{}"]) => RequestKind.OrdersByCustomer,
            ("GET", ["api", "orders", "event", "{}"]) => RequestKind.OrdersByEvent,
            _ => null
        };
    }
}