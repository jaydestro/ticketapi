internal static class LoadGenReportFormatter
{
    private const int WideLayoutMinimumWidth = 120;

    public static IReadOnlyList<string> FormatLiveDashboard(
        string profile,
        bool inBurst,
        int targetConcurrency,
        TimeSpan elapsed,
        TimeSpan interval,
        DashboardRenderContext runtime,
        MetricsSnapshot current,
        int width,
        DateTimeOffset reportTime)
    {
        var compact = width < WideLayoutMinimumWidth;
        var totals = current.Total;
        var workload = LoadGenProfiles.GetDisplayName(profile);
        var lines = new List<string>();
        if (compact)
        {
            var state = runtime.IsPaused ? "PAUSED" : "RUNNING";
            lines.Add(
                $"[{reportTime:HH:mm:ss}] Ticketing LoadGen | {workload.ToUpperInvariant()} | {state}");
            lines.Add(
                $"time {elapsed:hh\\:mm\\:ss} / {LoadGenRuntimeControls.FormatDuration(runtime.Duration),-9} " +
                $"conc {targetConcurrency}  active {totals.Active}  total RU {totals.RequestCharge:F1}");
            if (runtime.ShowControls)
            {
                lines.Add("Space pause | +/- concurrency | R reset | T time | Q quit");
            }
        }
        else
        {
            lines.Add(
                $"[{reportTime:HH:mm:ss}] workload={workload} " +
                $"mode={(inBurst ? "burst" : "steady")} concurrency={targetConcurrency} " +
                $"active={totals.Active} elapsed={elapsed:hh\\:mm\\:ss} " +
                $"total-ru={totals.RequestCharge:F1} state={(runtime.IsPaused ? "paused" : "running")}");
        }

        lines.Add(compact
            ? $"{"operation",-18} {"act",3} {"sent",5} {"ok",5} {"429",4} {"err",4} {"RU/q",7} {"p95",7}"
            : $"{"operation",-22} {"act",5} {"sent",7} {"ok",7} {"429",6} {"err",6} " +
              $"{"RU/query",9} {"avg ms",8} {"p95 ms",8} {"total RU",11}");

        foreach (var kind in LoadGenProfiles.GetRequestKinds(profile))
        {
            var value = current[kind];
            var errors = value.ClientErrors + value.ServerErrors + value.NetworkErrors;
            var averageMilliseconds = value.Completed > 0 ? value.TotalMilliseconds / value.Completed : 0;
            var averageRu = FormatRu(value.Charged, value.Completed, value.RequestCharge, divideBy: value.Charged);
            var totalRu = value.Charged > 0 ? $"{value.RequestCharge:F1}" : "-";
            var p95 = value.Completed > 0
                ? FormatLatency(MetricsCollector.GetPercentileMilliseconds(value.Histogram, 0.95))
                : "-";

            if (compact)
            {
                lines.Add(
                    $"{LoadGenNames.GetDisplayName(kind),-18} {value.Active,3:N0} {value.Sent,5:N0} " +
                    $"{value.Success,5:N0} {value.Throttled,4:N0} {errors,4:N0} " +
                    $"{averageRu,7} {p95,7}");
            }
            else
            {
                lines.Add(
                    $"{LoadGenNames.GetDisplayName(kind),-22} {value.Active,5:N0} {value.Sent,7:N0} " +
                    $"{value.Success,7:N0} {value.Throttled,6:N0} {errors,6:N0} " +
                    $"{averageRu,9} {averageMilliseconds,8:F0} {p95,8} {totalRu,11}");
            }
        }

        return lines;
    }

    public static IReadOnlyList<string> FormatFinal(
        string profile,
        TimeSpan elapsed,
        MetricsSnapshot snapshot,
        int width = int.MaxValue)
    {
        var compact = width < WideLayoutMinimumWidth;
        var workload = LoadGenProfiles.GetDisplayName(profile);
        var totals = snapshot.Total;
        var lines = new List<string>
        {
            string.Empty,
            $"=== final: {workload} {elapsed:hh\\:mm\\:ss} ===",
            $"Total used RU: {totals.RequestCharge:F1}",
            compact
                ? $"{"operation",-18} {"sent",5} {"ok",5} {"429",4} {"err",4} {"RU/q",7} {"p95",7}"
                : $"{"operation",-22} {"sent",9} {"done",9} {"2xx",9} {"429",7} {"4xx",7} {"5xx",7} {"net",7} " +
                  $"{"avg ms",9} {"p95 ms",9} {"avg RU",9} {"total RU",12}"
        };

        foreach (var kind in LoadGenProfiles.GetRequestKinds(profile))
        {
            var value = snapshot[kind];
            var averageMilliseconds = value.Completed > 0 ? value.TotalMilliseconds / value.Completed : 0;
            var averageRu = FormatRu(value.Charged, value.Completed, value.RequestCharge, divideBy: value.Charged);
            var totalRu = value.Charged > 0 ? $"{value.RequestCharge:F1}" : "-";
            var p95 = FormatLatency(MetricsCollector.GetPercentileMilliseconds(value.Histogram, 0.95));
            if (compact)
            {
                var errors = value.ClientErrors + value.ServerErrors + value.NetworkErrors;
                lines.Add(
                    $"{LoadGenNames.GetDisplayName(kind),-18} {value.Sent,5:N0} {value.Success,5:N0} " +
                    $"{value.Throttled,4:N0} {errors,4:N0} {averageRu,7} {p95,7}");
            }
            else
            {
                lines.Add(
                    $"{LoadGenNames.GetDisplayName(kind),-22} {value.Sent,9:N0} {value.Completed,9:N0} " +
                    $"{value.Success,9:N0} {value.Throttled,7:N0} {value.ClientErrors,7:N0} {value.ServerErrors,7:N0} " +
                    $"{value.NetworkErrors,7:N0} {averageMilliseconds,9:F0} {p95,9} " +
                    $"{averageRu,9} {totalRu,12}");
            }
        }

        var averageTotalRu = FormatRu(
            totals.Charged,
            totals.Completed,
            totals.RequestCharge,
            divideBy: totals.Charged);
        var totalRequestCharge = totals.Charged > 0 ? $"{totals.RequestCharge:F1}" : "-";
        var totalP95 = FormatLatency(MetricsCollector.GetPercentileMilliseconds(totals.Histogram, 0.95));
        if (compact)
        {
            var totalErrors = totals.ClientErrors + totals.ServerErrors + totals.NetworkErrors;
            lines.Add(
                $"TOTAL              {totals.Sent,5:N0} {totals.Success,5:N0} " +
                $"{totals.Throttled,4:N0} {totalErrors,4:N0} {averageTotalRu,7} {totalP95,7}");
        }
        else
        {
            lines.Add(
                $"TOTAL                  {totals.Sent,9:N0} {totals.Completed,9:N0} " +
                $"{totals.Success,9:N0} {totals.Throttled,7:N0} {totals.ClientErrors,7:N0} {totals.ServerErrors,7:N0} " +
                $"{totals.NetworkErrors,7:N0} {totals.AverageMilliseconds,9:F0} " +
                $"{totalP95,9} " +
                $"{averageTotalRu,9} {totalRequestCharge,12}");
        }

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