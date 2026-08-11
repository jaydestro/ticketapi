using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using static LoadGenConstants;

// Standalone traffic generator for the ticketing API. It reads the Cosmos DB RU charge header
// and prints recurring per-operation latency, status, and RU comparisons.
// Usage: LoadGen --concurrency <n> [--profile mixed|comparison] [--report-interval <seconds>]
//                [--seed <n>] [--duration <seconds>] [--base-url <url>]

var parsed = ParseArgs(args);
if (parsed is null)
{
    Console.Error.WriteLine(
        "Usage: LoadGen --concurrency <n> [--profile mixed|comparison] " +
        "[--report-interval <seconds>] [--seed <n>] [--duration <seconds>] [--base-url <url>]");
    return 1;
}

var (requestedSeed, baseConcurrency, durationSeconds, baseUrl, profile, reportIntervalSeconds) = parsed;
var instanceLock = TryAcquireInstanceLock();
if (instanceLock is null)
{
    Console.Error.WriteLine(
        "loadgen: another LoadGen process is already running on this machine. Stop it before starting another comparison.");
    return 3;
}
using var instanceLockHandle = instanceLock;
// --seed only controls RNG reproducibility for the traffic shape; random by default so it's not required.
var seed = requestedSeed ?? Random.Shared.Next();
var openEnded = durationSeconds is null;
var duration = durationSeconds is { } d ? TimeSpan.FromSeconds(d) : Timeout.InfiniteTimeSpan;

// Same city list the seeder uses, so "events by city" hits cities that actually exist.
string[] cities =
[
    "New York", "Los Angeles", "Chicago", "Houston", "Phoenix", "Philadelphia", "San Antonio",
    "San Diego", "Dallas", "Austin", "Jacksonville", "Fort Worth", "Columbus", "Charlotte",
    "San Francisco", "Indianapolis", "Seattle", "Denver", "Washington", "Boston", "Nashville",
    "Oklahoma City", "El Paso", "Portland", "Las Vegas", "Detroit", "Memphis", "Louisville",
    "Baltimore", "Milwaukee", "Albuquerque", "Tucson", "Fresno", "Sacramento", "Kansas City",
    "Atlanta", "Miami", "Raleigh", "Omaha", "Minneapolis"
];
string[] priceTiers = ["Economy", "Standard", "Premium", "VIP"];

// Read-heavy baseline, weighted toward the hot endpoints.
(RequestKind Kind, double Weight)[] baseWeights =
[
    (RequestKind.EventDetail, 30),
    (RequestKind.UpcomingEvents, 20),
    (RequestKind.EventsByCity, 15),
    (RequestKind.PurchaseTicket, 10),
    (RequestKind.OrdersByCustomer, 15),
    (RequestKind.OrdersByEvent, 7),
    (RequestKind.CreateEvent, 3)
];

// Burst mix: goal just scored, everyone piles into buying tickets and checking their orders.
(RequestKind Kind, double Weight)[] burstWeights =
[
    (RequestKind.EventDetail, 25),
    (RequestKind.UpcomingEvents, 5),
    (RequestKind.EventsByCity, 3),
    (RequestKind.PurchaseTicket, 45),
    (RequestKind.OrdersByCustomer, 15),
    (RequestKind.OrdersByEvent, 6),
    (RequestKind.CreateEvent, 1)
];

// Stable, read-only mix for before/after comparisons. It includes the query shapes previously
// responsible for the largest RU and latency costs, plus a point-read control.
(RequestKind Kind, double Weight)[] comparisonWeights =
[
    (RequestKind.EventDetail, 10),
    (RequestKind.UpcomingEvents, 20),
    (RequestKind.EventsByCity, 20),
    (RequestKind.OrdersByCustomer, 20),
    (RequestKind.OrdersByEvent, 30)
];

var baseWeightTotal = baseWeights.Sum(w => w.Weight);
var burstWeightTotal = burstWeights.Sum(w => w.Weight);
var comparisonWeightTotal = comparisonWeights.Sum(w => w.Weight);

using var http = new HttpClient(new SocketsHttpHandler
{
    MaxConnectionsPerServer = 4_000,
    PooledConnectionLifetime = TimeSpan.FromMinutes(5)
})
{
    BaseAddress = new Uri(baseUrl),
    Timeout = TimeSpan.FromSeconds(30)
};

var accessToken = Environment.GetEnvironmentVariable("TICKETING_API_ACCESS_TOKEN");
if (!string.IsNullOrWhiteSpace(accessToken))
{
    if (!http.BaseAddress.IsLoopback && http.BaseAddress.Scheme != Uri.UriSchemeHttps)
    {
        Console.Error.WriteLine("loadgen: bearer tokens require HTTPS for non-loopback targets.");
        return 1;
    }

    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
}

// Literal paths this generator was written against; used whenever OpenAPI discovery can't confirm otherwise.
Dictionary<RequestKind, string> defaultRoutes = new()
{
    [RequestKind.EventDetail] = "/api/events/{id}",
    [RequestKind.UpcomingEvents] = "/api/events/upcoming",
    [RequestKind.EventsByCity] = "/api/events/city/{city}",
    [RequestKind.PurchaseTicket] = "/api/orders",
    [RequestKind.OrdersByCustomer] = "/api/orders/customer/{customerId}",
    [RequestKind.OrdersByEvent] = "/api/orders/event/{eventId}",
    [RequestKind.CreateEvent] = "/api/events"
};

var routes = new Dictionary<RequestKind, string>(defaultRoutes);
if (!await DiscoverRoutesAsync(http, routes, profile))
{
    return 2;
}

var rng = new Random(seed);
var stopwatch = Stopwatch.StartNew();
var metrics = new MetricsCollector();

// Ctrl+C should stop launching new requests and drain in-flight ones, not kill the process outright.
using var stopRequested = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stopRequested.Cancel();
};

var inFlight = new List<Task>();
var lastReportElapsed = TimeSpan.Zero;
var previousSnapshot = metrics.Snapshot();
var dashboard = new LiveDashboard();
AppDomain.CurrentDomain.ProcessExit += (_, _) => dashboard.Complete();

Console.WriteLine(
    $"loadgen: profile={profile} seed={seed} concurrency={baseConcurrency} " +
    $"duration={(openEnded ? "until Ctrl+C" : $"{durationSeconds}s")} target={baseUrl}");
if (profile == LoadProfiles.Comparison)
{
    Console.WriteLine(
        "loadgen: comparison profile is read-only and targets point read, upcoming, city, " +
        "customer-order, and hot-event-order queries");
}

while (!stopRequested.IsCancellationRequested && (openEnded || stopwatch.Elapsed < duration))
{
    inFlight.RemoveAll(t => t.IsCompleted);

    var elapsed = stopwatch.Elapsed;
    var cyclePosition = elapsed.TotalSeconds % BurstPeriodSeconds;
    var inBurst = profile == LoadProfiles.Mixed && cyclePosition < BurstDurationSeconds;
    var targetConcurrency = inBurst ? baseConcurrency * BurstMultiplier : baseConcurrency;

    while (inFlight.Count < targetConcurrency && !stopRequested.IsCancellationRequested &&
           (openEnded || stopwatch.Elapsed < duration))
    {
        var (kind, method, path, body) = NextRequest(rng, inBurst, profile);
        inFlight.Add(SendAsync(http, kind, method, path, body));
        metrics.RecordSent(kind);
    }

    if ((elapsed - lastReportElapsed).TotalSeconds >= reportIntervalSeconds)
    {
        var currentSnapshot = metrics.Snapshot();
        PrintIntervalReport(
            dashboard,
            profile,
            inBurst,
            targetConcurrency,
            elapsed,
            elapsed - lastReportElapsed,
            previousSnapshot,
            currentSnapshot);
        previousSnapshot = currentSnapshot;
        lastReportElapsed = elapsed;
    }

    await Task.Delay(20);
}

dashboard.Complete();
Console.WriteLine($"loadgen: stopping - draining {inFlight.Count} in-flight request(s)...");
await Task.WhenAll(inFlight);

var finalSnapshot = metrics.Snapshot();
PrintFinalReport(profile, stopwatch.Elapsed, finalSnapshot);
if (finalSnapshot.Total.Success == 0)
{
    Console.Error.WriteLine("loadgen: no requests succeeded; check the API routes, data, and access token.");
    return 4;
}

return 0;

(RequestKind Kind, HttpMethod Method, string Path, string? Body) NextRequest(
    Random r,
    bool burst,
    string selectedProfile)
{
    var weights = selectedProfile == LoadProfiles.Comparison
        ? comparisonWeights
        : burst ? burstWeights : baseWeights;
    var total = selectedProfile == LoadProfiles.Comparison
        ? comparisonWeightTotal
        : burst ? burstWeightTotal : baseWeightTotal;
    var kind = PickKind(r, weights, total);

    var (method, path, body) = kind switch
    {
        RequestKind.EventDetail =>
            (HttpMethod.Get, BuildPath(
                routes[kind],
                selectedProfile == LoadProfiles.Comparison ? ChampionshipEventId : PickEventId(r)), (string?)null),

        RequestKind.UpcomingEvents =>
            (HttpMethod.Get, routes[kind], null),

        RequestKind.EventsByCity =>
            (HttpMethod.Get, BuildPath(
                routes[kind],
                selectedProfile == LoadProfiles.Comparison ? ComparisonCity : cities[r.Next(cities.Length)]), null),

        RequestKind.PurchaseTicket =>
            (HttpMethod.Post, routes[kind], JsonSerializer.Serialize(new
            {
                eventId = PickEventId(r),
                customerId = PickCustomerId(r),
                quantity = r.Next(1, 5)
            })),

        RequestKind.OrdersByCustomer =>
            (HttpMethod.Get, BuildPath(
                routes[kind],
                selectedProfile == LoadProfiles.Comparison ? ComparisonCustomerId : PickCustomerId(r)), null),

        RequestKind.OrdersByEvent =>
            (HttpMethod.Get, BuildPath(
                routes[kind],
                selectedProfile == LoadProfiles.Comparison ? ChampionshipEventId : PickEventId(r)), null),

        RequestKind.CreateEvent =>
            (HttpMethod.Post, routes[kind], JsonSerializer.Serialize(new
            {
                name = $"Load Test Event {r.Next(1_000_000)}",
                venue = "Load Test Arena",
                city = cities[r.Next(cities.Length)],
                eventDate = DateTime.UtcNow.AddDays(r.Next(1, 365)),
                totalSeats = r.Next(500, 20_000),
                priceTier = priceTiers[r.Next(priceTiers.Length)]
            })),

        _ => throw new InvalidOperationException($"Unhandled request kind: {kind}")
    };

    return (kind, method, path, body);
}

// Substitutes the last (parameter) segment of a route template, e.g. "/api/events/{id}" + "event-00001".
string BuildPath(string template, string value)
{
    var segments = template.Split('/');
    segments[^1] = Uri.EscapeDataString(value);
    return string.Join('/', segments);
}

// Reads the API's OpenAPI document and requires every route used by the selected profile.
async Task<bool> DiscoverRoutesAsync(
    HttpClient client,
    Dictionary<RequestKind, string> targetRoutes,
    string selectedProfile)
{
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var response = await client.GetAsync("/openapi/v1.json", cts.Token);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized
                or System.Net.HttpStatusCode.Forbidden)
            {
                Console.WriteLine(
                    $"loadgen: OpenAPI discovery returned {(int)response.StatusCode} " +
                    $"({response.StatusCode}); set TICKETING_API_ACCESS_TOKEN to a token with " +
                    "Ticketing.Read access. Stopping");
                return false;
            }
            Console.WriteLine(
                $"loadgen: OpenAPI discovery returned {(int)response.StatusCode} " +
                $"({response.StatusCode}). Stopping");
            return false;
        }

        using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);

        if (!doc.RootElement.TryGetProperty("paths", out var paths))
        {
            Console.WriteLine("loadgen: OpenAPI document has no paths. Stopping");
            return false;
        }

        var discovered = new Dictionary<RequestKind, string>();
        foreach (var pathProp in paths.EnumerateObject())
        {
            var segments = pathProp.Name.Trim('/').Split('/');
            foreach (var methodProp in pathProp.Value.EnumerateObject())
            {
                var kind = MatchKind(methodProp.Name, segments);
                if (kind is { } k && !discovered.ContainsKey(k))
                {
                    discovered[k] = pathProp.Name;
                }
            }
        }

        var mismatches = new List<string>();
        RequestKind[] requiredKinds = selectedProfile == LoadProfiles.Comparison
            ?
            [
                RequestKind.EventDetail,
                RequestKind.UpcomingEvents,
                RequestKind.EventsByCity,
                RequestKind.OrdersByCustomer,
                RequestKind.OrdersByEvent
            ]
            : Enum.GetValues<RequestKind>();
        var missingKinds = requiredKinds.Where(kind => !discovered.ContainsKey(kind)).ToArray();
        if (missingKinds.Length > 0)
        {
            Console.WriteLine(
                $"loadgen: OpenAPI is missing required {selectedProfile} route(s): " +
                string.Join(", ", missingKinds.Select(GetDisplayName)) + ". Stopping");
            return false;
        }

        foreach (var (kind, path) in discovered)
        {
            // ASP.NET Core routing is case-insensitive, so only flag genuine route changes, not casing.
            if (!string.Equals(targetRoutes[kind], path, StringComparison.OrdinalIgnoreCase))
            {
                mismatches.Add($"{kind}: {targetRoutes[kind]} -> {path}");
            }

            targetRoutes[kind] = path;
        }

        var total = Enum.GetValues<RequestKind>().Length;
        Console.WriteLine($"loadgen: discovered {discovered.Count}/{total} routes from OpenAPI" +
            (mismatches.Count > 0 ? $", {mismatches.Count} differ from defaults:" : " (all match defaults)"));
        foreach (var m in mismatches)
        {
            Console.WriteLine($"  {m}");
        }

        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"loadgen: OpenAPI discovery failed ({ex.GetType().Name}). Stopping");
        return false;
    }
}

// Maps an OpenAPI (method, path segments) pair to the request kind it represents, if any.
RequestKind? MatchKind(string method, string[] segments)
{
    static bool IsParam(string s) => s.StartsWith('{') && s.EndsWith('}');
    var shape = segments.Select(s => IsParam(s) ? "{}" : s.ToLowerInvariant()).ToArray();
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

RequestKind PickKind(Random r, (RequestKind Kind, double Weight)[] weights, double total)
{
    var roll = r.NextDouble() * total;
    var accumulated = 0.0;
    foreach (var (kind, weight) in weights)
    {
        accumulated += weight;
        if (roll < accumulated)
        {
            return kind;
        }
    }

    return weights[^1].Kind;
}

// The championship final (event-00001) draws the overwhelming majority of traffic;
// everything else is a long tail spread uniformly across all 5,000 events.
string PickEventId(Random r) =>
    r.NextDouble() < ChampionshipShare
        ? ChampionshipEventId
        : $"event-{r.Next(1, EventCount + 1):D5}";

string PickCustomerId(Random r) => $"customer-{r.Next(1, CustomerCount + 1):D5}";

async Task SendAsync(HttpClient client, RequestKind kind, HttpMethod method, string path, string? jsonBody)
{
    var requestStopwatch = Stopwatch.StartNew();
    try
    {
        using var request = new HttpRequestMessage(method, path);
        if (kind == RequestKind.PurchaseTicket)
        {
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        }

        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        double? charge = null;
        if (response.Headers.TryGetValues("x-ms-request-charge", out var values) &&
            double.TryParse(
                values.FirstOrDefault(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsedCharge))
        {
            charge = parsedCharge;
        }

        await response.Content.CopyToAsync(Stream.Null);
        requestStopwatch.Stop();
        metrics.RecordCompleted(kind, (int)response.StatusCode, charge, requestStopwatch.Elapsed);
    }
    catch (Exception)
    {
        requestStopwatch.Stop();
        metrics.RecordNetworkFailure(kind, requestStopwatch.Elapsed);
    }
}

void PrintIntervalReport(
    LiveDashboard liveDashboard,
    string selectedProfile,
    bool inBurst,
    int targetConcurrency,
    TimeSpan elapsed,
    TimeSpan interval,
    MetricsSnapshot previous,
    MetricsSnapshot current)
{
    var lines = new List<string>
    {
        $"[{DateTimeOffset.Now:HH:mm:ss}] profile={selectedProfile} " +
        $"mode={(inBurst ? "burst" : "steady")} concurrency={targetConcurrency} " +
        $"elapsed={elapsed:hh\\:mm\\:ss}",
        $"{"operation",-22} {"req/s",7} {"2xx",7} {"4xx",6} {"5xx",6} {"net",5} " +
        $"{"avg ms",8} {"p95 ms",8} {"avg RU",8} {"RU/s",9} {"total RU",11}"
    };

    foreach (var kind in Enum.GetValues<RequestKind>())
    {
        var delta = current[kind] - previous[kind];
        var seconds = Math.Max(interval.TotalSeconds, 0.001);
        var avgMilliseconds = delta.Completed > 0
            ? delta.TotalMilliseconds / delta.Completed
            : 0;
        var avgRu = delta.Charged > 0 ? $"{delta.RequestCharge / delta.Charged:F2}" : "N/A";
        var ruPerSecond = delta.Charged > 0 ? $"{delta.RequestCharge / seconds:F1}" : "N/A";
        var totalRu = current[kind].Charged > 0 ? $"{current[kind].RequestCharge:F1}" : "N/A";
        var p95 = MetricsCollector.GetPercentileMilliseconds(delta.Histogram, 0.95);

        lines.Add(
            $"{GetDisplayName(kind),-22} {delta.Completed / seconds,7:F1} {delta.Success,7:N0} " +
            $"{delta.ClientErrors,6:N0} {delta.ServerErrors,6:N0} {delta.NetworkErrors,5:N0} " +
            $"{avgMilliseconds,8:F0} {p95,8:F0} {avgRu,8} {ruPerSecond,9} {totalRu,11}");
    }

    liveDashboard.Render(lines);
}

void PrintFinalReport(string selectedProfile, TimeSpan elapsed, MetricsSnapshot snapshot)
{
    Console.WriteLine();
    Console.WriteLine($"=== LoadGen final comparison: {selectedProfile} ({elapsed:hh\\:mm\\:ss}) ===");
    Console.WriteLine(
        $"{"operation",-22} {"sent",9} {"done",9} {"2xx",9} {"4xx",7} {"5xx",7} {"net",7} " +
        $"{"avg ms",9} {"p95 ms",9} {"avg RU",9} {"total RU",12}");

    foreach (var kind in Enum.GetValues<RequestKind>())
    {
        var value = snapshot[kind];
        var avgMilliseconds = value.Completed > 0
            ? value.TotalMilliseconds / value.Completed
            : 0;
        var avgRu = value.Charged > 0 ? $"{value.RequestCharge / value.Charged:F2}" : "N/A";
        var totalRu = value.Charged > 0 ? $"{value.RequestCharge:F1}" : "N/A";
        var p95 = MetricsCollector.GetPercentileMilliseconds(value.Histogram, 0.95);
        Console.WriteLine(
            $"{GetDisplayName(kind),-22} {value.Sent,9:N0} {value.Completed,9:N0} " +
            $"{value.Success,9:N0} {value.ClientErrors,7:N0} {value.ServerErrors,7:N0} " +
            $"{value.NetworkErrors,7:N0} {avgMilliseconds,9:F0} {p95,9:F0} " +
                $"{avgRu,9} {totalRu,12}");
    }

    var totals = snapshot.Total;
            var totalAverageRu = totals.Charged > 0 ? $"{totals.RequestCharge / totals.Charged:F2}" : "N/A";
            var totalRequestCharge = totals.Charged > 0 ? $"{totals.RequestCharge:F1}" : "N/A";
    Console.WriteLine(
        $"TOTAL                  {totals.Sent,9:N0} {totals.Completed,9:N0} " +
        $"{totals.Success,9:N0} {totals.ClientErrors,7:N0} {totals.ServerErrors,7:N0} " +
        $"{totals.NetworkErrors,7:N0} {totals.AverageMilliseconds,9:F0} " +
        $"{MetricsCollector.GetPercentileMilliseconds(totals.Histogram, 0.95),9:F0} " +
        $"{totalAverageRu,9} {totalRequestCharge,12}");
}

string GetDisplayName(RequestKind kind) => kind switch
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

FileStream? TryAcquireInstanceLock()
{
    try
    {
        var lockPath = Path.Combine(Path.GetTempPath(), "ticketapi-loadgen.lock");
        return new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.DeleteOnClose);
    }
    catch (IOException)
    {
        return null;
    }
}

LoadGenOptions? ParseArgs(string[] a)
{
    int? seed = null;
    int? concurrency = null;
    double? parsedDuration = null;
    var url = "http://localhost:5107";
    var selectedProfile = LoadProfiles.Mixed;
    var reportInterval = 2.0;

    for (var i = 0; i < a.Length; i++)
    {
        switch (a[i])
        {
            case "--seed" when i + 1 < a.Length && int.TryParse(a[i + 1], out var s):
                seed = s;
                i++;
                break;
            case "--concurrency" when i + 1 < a.Length && int.TryParse(a[i + 1], out var c):
                concurrency = c;
                i++;
                break;
            case "--duration" when i + 1 < a.Length &&
                double.TryParse(
                    a[i + 1],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var dur):
                parsedDuration = dur;
                i++;
                break;
            case "--base-url" when i + 1 < a.Length:
                url = a[i + 1];
                i++;
                break;
            case "--profile" when i + 1 < a.Length &&
                a[i + 1] is LoadProfiles.Mixed or LoadProfiles.Comparison:
                selectedProfile = a[i + 1];
                i++;
                break;
            case "--report-interval" when i + 1 < a.Length &&
                double.TryParse(
                    a[i + 1],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var interval) && interval is >= 0.5 and <= 60:
                reportInterval = interval;
                i++;
                break;
            default:
                return null;
        }
    }

    return concurrency is null or < 1 or > 4_000 ||
        parsedDuration is <= 0 ||
        (parsedDuration is { } duration && !double.IsFinite(duration)) ||
        parsedDuration > TimeSpan.MaxValue.TotalSeconds ||
        !Uri.TryCreate(url, UriKind.Absolute, out var baseUri) ||
        (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
        ? null
        : new LoadGenOptions(
            seed,
            concurrency.Value,
            parsedDuration,
            url,
            selectedProfile,
            reportInterval);
}

internal enum RequestKind
{
    EventDetail,
    UpcomingEvents,
    EventsByCity,
    PurchaseTicket,
    OrdersByCustomer,
    OrdersByEvent,
    CreateEvent
}

internal sealed record LoadGenOptions(
    int? Seed,
    int Concurrency,
    double? DurationSeconds,
    string BaseUrl,
    string Profile,
    double ReportIntervalSeconds);

internal static class LoadProfiles
{
    public const string Mixed = "mixed";
    public const string Comparison = "comparison";
}

internal static class LoadGenConstants
{
    public const string ChampionshipEventId = "event-00001";
    public const int EventCount = 5_000;
    public const int CustomerCount = 25_000;
    public const double ChampionshipShare = 0.75;
    public const int BurstPeriodSeconds = 30;
    public const int BurstDurationSeconds = 5;
    public const int BurstMultiplier = 10;
    public const string ComparisonCity = "Memphis";
    public const string ComparisonCustomerId = "customer-00001";
}

internal sealed class MetricsCollector
{
    private static readonly double[] LatencyUpperBounds =
        [5, 10, 25, 50, 100, 250, 500, 1_000, 2_500, 5_000, 10_000, 30_000, double.MaxValue];

    private readonly OperationMetrics[] _metrics =
        Enum.GetValues<RequestKind>().Select(_ => new OperationMetrics(LatencyUpperBounds.Length)).ToArray();

    public void RecordSent(RequestKind kind) => _metrics[(int)kind].RecordSent();

    public void RecordCompleted(
        RequestKind kind,
        int statusCode,
        double? requestCharge,
        TimeSpan elapsed) =>
        _metrics[(int)kind].RecordCompleted(statusCode, requestCharge, elapsed, LatencyUpperBounds);

    public void RecordNetworkFailure(RequestKind kind, TimeSpan elapsed) =>
        _metrics[(int)kind].RecordNetworkFailure(elapsed, LatencyUpperBounds);

    public MetricsSnapshot Snapshot() => new(_metrics.Select(metric => metric.Snapshot()).ToArray());

    public static double GetPercentileMilliseconds(IReadOnlyList<long> histogram, double percentile)
    {
        var count = histogram.Sum();
        if (count == 0)
        {
            return 0;
        }

        var target = (long)Math.Ceiling(count * percentile);
        var cumulative = 0L;
        for (var index = 0; index < histogram.Count; index++)
        {
            cumulative += histogram[index];
            if (cumulative >= target)
            {
                return LatencyUpperBounds[index];
            }
        }

        return LatencyUpperBounds[^1];
    }
}

internal sealed class OperationMetrics(int histogramSize)
{
    private readonly object _sync = new();
    private readonly long[] _histogram = new long[histogramSize];
    private long _sent;
    private long _completed;
    private long _success;
    private long _clientErrors;
    private long _serverErrors;
    private long _networkErrors;
    private long _charged;
    private double _requestCharge;
    private double _totalMilliseconds;

    public void RecordSent()
    {
        lock (_sync)
        {
            _sent++;
        }
    }

    public void RecordCompleted(
        int statusCode,
        double? requestCharge,
        TimeSpan elapsed,
        IReadOnlyList<double> latencyUpperBounds)
    {
        lock (_sync)
        {
            _completed++;
            if (statusCode is >= 200 and < 300)
            {
                _success++;
            }
            else if (statusCode < 500)
            {
                _clientErrors++;
            }
            else
            {
                _serverErrors++;
            }

            if (requestCharge is { } charge)
            {
                _charged++;
                _requestCharge += charge;
            }

            RecordLatency(elapsed, latencyUpperBounds);
        }
    }

    public void RecordNetworkFailure(TimeSpan elapsed, IReadOnlyList<double> latencyUpperBounds)
    {
        lock (_sync)
        {
            _completed++;
            _networkErrors++;
            RecordLatency(elapsed, latencyUpperBounds);
        }
    }

    public OperationMetricsSnapshot Snapshot()
    {
        lock (_sync)
        {
            return new OperationMetricsSnapshot(
                _sent,
                _completed,
                _success,
                _clientErrors,
                _serverErrors,
                _networkErrors,
                _charged,
                _requestCharge,
                _totalMilliseconds,
                [.. _histogram]);
        }
    }

    private void RecordLatency(TimeSpan elapsed, IReadOnlyList<double> latencyUpperBounds)
    {
        var milliseconds = elapsed.TotalMilliseconds;
        _totalMilliseconds += milliseconds;
        var bucket = 0;
        while (bucket < latencyUpperBounds.Count - 1 && milliseconds > latencyUpperBounds[bucket])
        {
            bucket++;
        }
        _histogram[bucket]++;
    }
}

internal sealed record OperationMetricsSnapshot(
    long Sent,
    long Completed,
    long Success,
    long ClientErrors,
    long ServerErrors,
    long NetworkErrors,
    long Charged,
    double RequestCharge,
    double TotalMilliseconds,
    long[] Histogram)
{
    public static OperationMetricsSnapshot operator -(
        OperationMetricsSnapshot current,
        OperationMetricsSnapshot previous) => new(
            current.Sent - previous.Sent,
            current.Completed - previous.Completed,
            current.Success - previous.Success,
            current.ClientErrors - previous.ClientErrors,
            current.ServerErrors - previous.ServerErrors,
            current.NetworkErrors - previous.NetworkErrors,
            current.Charged - previous.Charged,
            current.RequestCharge - previous.RequestCharge,
            current.TotalMilliseconds - previous.TotalMilliseconds,
            current.Histogram.Zip(previous.Histogram, (left, right) => left - right).ToArray());
}

internal sealed record MetricsSnapshot(OperationMetricsSnapshot[] Operations)
{
    public OperationMetricsSnapshot this[RequestKind kind] => Operations[(int)kind];

    public MetricsTotal Total => new(
        Operations.Sum(value => value.Sent),
        Operations.Sum(value => value.Completed),
        Operations.Sum(value => value.Success),
        Operations.Sum(value => value.ClientErrors),
        Operations.Sum(value => value.ServerErrors),
        Operations.Sum(value => value.NetworkErrors),
        Operations.Sum(value => value.Charged),
        Operations.Sum(value => value.RequestCharge),
        Operations.Sum(value => value.TotalMilliseconds),
        Enumerable.Range(0, Operations[0].Histogram.Length)
            .Select(index => Operations.Sum(value => value.Histogram[index]))
            .ToArray());
}

internal sealed record MetricsTotal(
    long Sent,
    long Completed,
    long Success,
    long ClientErrors,
    long ServerErrors,
    long NetworkErrors,
    long Charged,
    double RequestCharge,
    double TotalMilliseconds,
    long[] Histogram)
{
    public double AverageMilliseconds => Completed > 0 ? TotalMilliseconds / Completed : 0;
    public double AverageRu => Charged > 0 ? RequestCharge / Charged : 0;
}

internal sealed class LiveDashboard
{
    private bool _interactive = !Console.IsOutputRedirected;
    private bool _started;
    private bool _completed;

    public void Render(IReadOnlyList<string> lines)
    {
        if (!_interactive)
        {
            foreach (var line in lines)
            {
                Console.WriteLine(line);
            }
            return;
        }

        try
        {
            if (!_started)
            {
                // Use the terminal's alternate screen buffer, like top/htop. The user's normal
                // scrollback is restored when Complete is called.
                Console.Write("\u001b[?1049h\u001b[2J\u001b[H\u001b[?25l");
                _started = true;
            }

            Console.Write("\u001b[H");
            foreach (var line in lines)
            {
                Console.Write("\u001b[2K");
                Console.WriteLine(line);
            }
            Console.Write("\u001b[J");
            Console.Out.Flush();
        }
        catch (IOException)
        {
            _interactive = false;
            RestoreTerminal();
            Console.WriteLine();
            foreach (var line in lines)
            {
                Console.WriteLine(line);
            }
        }
    }

    public void Complete()
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        RestoreTerminal();
    }

    private void RestoreTerminal()
    {
        if (!_started)
        {
            return;
        }
        try
        {
            Console.Write("\u001b[?25h\u001b[?1049l");
            Console.Out.Flush();
            _started = false;
        }
        catch (IOException)
        {
            _started = false;
        }
    }
}
