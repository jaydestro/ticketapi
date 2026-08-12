using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using static LoadGenConstants;

// Standalone traffic generator for the ticketing API. It reads the Cosmos DB RU charge header
// and prints recurring per-operation latency, status, and RU comparisons.
// Usage: LoadGen --concurrency <n> [--profile mixed|comparison] [--report-interval <seconds>]
//                [--request-timeout <seconds>] [--seed <n>] [--duration <seconds>]
//                [--base-url <url>] [--saturate]

var parsed = ParseArgs(args);
if (parsed is null)
{
    Console.Error.WriteLine(
        "Usage: LoadGen --concurrency <n> [--profile mixed|comparison] " +
        "[--report-interval <seconds>] [--seed <n>] [--duration <seconds>] " +
        "[--request-timeout <seconds>] [--base-url <url>] [--saturate]");
    return 1;
}

var (requestedSeed, baseConcurrency, durationSeconds, baseUrl, profile, reportIntervalSeconds, requestTimeoutSeconds, saturate) = parsed;
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
var initialDuration = durationSeconds is { } d ? TimeSpan.FromSeconds(d) : (TimeSpan?)null;

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

// Stable, read-only mix for repeatable comparisons. It includes the query shapes expected to
// produce the largest RU and latency costs, plus a point-read control.
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
    Timeout = TimeSpan.FromSeconds(requestTimeoutSeconds)
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
var refreshStopwatch = Stopwatch.StartNew();
var metrics = new MetricsCollector();
var controls = new LoadGenRuntimeControls(baseConcurrency, initialDuration);
var saturation = new LoadGenSaturationController(saturate, baseConcurrency);

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
var workloadName = LoadGenProfiles.GetDisplayName(profile);

Console.WriteLine(
    $"loadgen: workload={workloadName} seed={seed} concurrency={baseConcurrency} " +
    $"duration={LoadGenRuntimeControls.FormatDuration(initialDuration)} " +
    $"request-timeout={requestTimeoutSeconds}s saturation={(saturate ? "adaptive" : "off")} target={baseUrl}");
if (profile == LoadProfiles.Comparison)
{
    Console.WriteLine(
        "loadgen: comparison profile is read-only and targets point read, upcoming, city, " +
        "customer-order, and hot-event-order queries");
}

var mandatoryKinds = new Queue<RequestKind>(LoadGenProfiles.GetRequestKinds(profile));
if (!saturate)
{
    // Normal runs start one request for every operation so short diagnostics do not rely on chance.
    while (mandatoryKinds.TryDequeue(out var kind))
    {
        var (method, path, body) = CreateRequest(kind, rng, profile);
        inFlight.Add(SendAsync(http, kind, method, path, body));
        metrics.RecordSent(kind);
    }
}

while (!stopRequested.IsCancellationRequested &&
       (controls.Duration is null || stopwatch.Elapsed < controls.Duration))
{
    inFlight.RemoveAll(t => t.IsCompleted);
    ProcessPendingKeys();

    var elapsed = stopwatch.Elapsed;
    var cyclePosition = elapsed.TotalSeconds % BurstPeriodSeconds;
    var inBurst = !saturate && profile == LoadProfiles.Mixed && cyclePosition < BurstDurationSeconds;
    var targetConcurrency = saturate
        ? saturation.TargetConcurrency
        : inBurst
        ? Math.Min(4_000, controls.Concurrency * BurstMultiplier)
        : controls.Concurrency;

    while (!controls.IsPaused &&
           inFlight.Count < targetConcurrency && !stopRequested.IsCancellationRequested &&
            (controls.Duration is null || stopwatch.Elapsed < controls.Duration))
    {
        var kind = mandatoryKinds.TryDequeue(out var mandatoryKind)
            ? mandatoryKind
            : PickKind(rng, inBurst, profile);
        var (method, path, body) = CreateRequest(kind, rng, profile);
        inFlight.Add(SendAsync(http, kind, method, path, body));
        metrics.RecordSent(kind);
    }

    var refreshElapsed = refreshStopwatch.Elapsed;
    if ((refreshElapsed - lastReportElapsed).TotalSeconds >= reportIntervalSeconds)
    {
        var currentSnapshot = metrics.Snapshot();
        saturation.ObserveAndAdvance(currentSnapshot.Total.Throttled, controls.IsPaused);
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
        lastReportElapsed = refreshElapsed;
    }

    await Task.Delay(20);
}

dashboard.Complete();
Console.WriteLine($"loadgen: stopping - draining {inFlight.Count} in-flight request(s)...");
await Task.WhenAll(inFlight);

var finalSnapshot = metrics.Snapshot();
PrintFinalReport(profile, stopwatch.Elapsed, finalSnapshot, dashboard.Width);
if (finalSnapshot.Total.Success == 0)
{
    Console.Error.WriteLine("loadgen: no requests succeeded; check the API routes, data, and access token.");
    return 4;
}


var unsuccessfulOperations = LoadGenProfiles.GetRequestKinds(profile)
    .Where(kind => finalSnapshot[kind].Success == 0)
    .ToArray();
if (unsuccessfulOperations.Length > 0)
{
    Console.Error.WriteLine(
        "loadgen: selected operation(s) had no successful requests: " +
        string.Join(", ", unsuccessfulOperations.Select(LoadGenNames.GetDisplayName)) + ".");
    return 5;
}

return 0;

void ProcessPendingKeys()
{
    while (dashboard.TryReadKey(out var key))
    {
        var previousConcurrency = controls.Concurrency;
        switch (controls.Handle(key))
        {
            case LoadGenControlAction.Paused:
                stopwatch.Stop();
                break;
            case LoadGenControlAction.Resumed:
                stopwatch.Start();
                break;
            case LoadGenControlAction.Reset:
                metrics.Reset();
                previousSnapshot = metrics.Snapshot();
                if (controls.IsPaused)
                {
                    stopwatch.Reset();
                }
                else
                {
                    stopwatch.Restart();
                }
                break;
            case LoadGenControlAction.Stop:
                stopRequested.Cancel();
                break;
        }

        if (saturation.Enabled && controls.Concurrency != previousConcurrency)
            {
            saturation.SetTarget(controls.Concurrency);
            }
    }
}

RequestKind PickKind(
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
    return PickWeightedKind(r, weights, total);
}

(HttpMethod Method, string Path, string? Body) CreateRequest(
    RequestKind kind,
    Random r,
    string selectedProfile)
{
    return kind switch
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
                var kind = LoadGenRoutes.MatchKind(methodProp.Name, segments);
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
                string.Join(", ", missingKinds.Select(LoadGenNames.GetDisplayName)) + ". Stopping");
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

RequestKind PickWeightedKind(Random r, (RequestKind Kind, double Weight)[] weights, double total)
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
        if ((int)response.StatusCode == 429)
        {
            saturation.ObserveThrottling();
        }

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

        var queryScope = response.Headers.TryGetValues("x-cosmos-query-scope", out var scopeValues)
            ? CosmosQueryScopeExtensions.Parse(scopeValues.FirstOrDefault())
            : CosmosQueryScope.Unknown;

        await response.Content.CopyToAsync(Stream.Null);
        requestStopwatch.Stop();
        metrics.RecordCompleted(
            kind,
            (int)response.StatusCode,
            charge,
            queryScope,
            requestStopwatch.Elapsed);
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
    liveDashboard.Render(LoadGenLiveDashboardFormatter.Format(
        selectedProfile,
        inBurst,
        targetConcurrency,
        elapsed,
        new DashboardRenderContext(
            controls.IsPaused,
            controls.Duration,
            liveDashboard.IsInteractive,
            saturation.Enabled,
            saturation.ThrottlingObserved,
            saturation.IsAtMaximum),
        current,
        liveDashboard.Width,
        DateTimeOffset.Now));
}

void PrintFinalReport(
    string selectedProfile,
    TimeSpan elapsed,
    MetricsSnapshot snapshot,
    int width)
{
    foreach (var line in LoadGenLiveDashboardFormatter.FormatFinal(
        selectedProfile,
        elapsed,
        snapshot,
        width))
    {
        Console.WriteLine(line);
    }
}

FileStream? TryAcquireInstanceLock()
{
    try
    {
        var configuredLockPath = Environment.GetEnvironmentVariable("TICKETING_LOADGEN_LOCK_PATH");
        var lockPath = string.IsNullOrWhiteSpace(configuredLockPath)
            ? Path.Combine(Path.GetTempPath(), "ticketapi-loadgen.lock")
            : configuredLockPath;
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
    var reportInterval = 0.5;
    var requestTimeout = 120.0;
    var saturate = false;

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
            case "--request-timeout" when i + 1 < a.Length &&
                double.TryParse(
                    a[i + 1],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var timeout) && timeout is >= 1 and <= 600:
                requestTimeout = timeout;
                i++;
                break;
            case "--saturate":
                saturate = true;
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
            reportInterval,
            requestTimeout,
            saturate);
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
    double ReportIntervalSeconds,
    double RequestTimeoutSeconds,
    bool Saturate);

internal sealed record DashboardRenderContext(
    bool IsPaused,
    TimeSpan? Duration,
    bool ShowControls,
    bool IsSaturating = false,
    bool ThrottlingObserved = false,
    bool SaturationAtMaximum = false);

internal static class LoadProfiles
{
    public const string Mixed = "mixed";
    public const string Comparison = "comparison";
}

internal static class LoadGenProfiles
{
    private static readonly RequestKind[] ComparisonKinds =
    [
        RequestKind.EventDetail,
        RequestKind.UpcomingEvents,
        RequestKind.EventsByCity,
        RequestKind.OrdersByCustomer,
        RequestKind.OrdersByEvent
    ];

    private static readonly RequestKind[] MixedKinds = Enum.GetValues<RequestKind>();

    public static IReadOnlyList<RequestKind> GetRequestKinds(string profile) =>
        profile == LoadProfiles.Comparison ? ComparisonKinds : MixedKinds;

    public static string GetDisplayName(string profile) =>
        profile == LoadProfiles.Comparison ? "read" : profile;
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
    public const int MaximumConcurrency = 4_000;
    public const string ComparisonCity = "Memphis";
    public const string ComparisonCustomerId = "customer-00001";
}

internal sealed class MetricsCollector
{
    private static readonly double[] LatencyUpperBounds = BuildLatencyUpperBounds();

    private readonly OperationMetrics[] _metrics =
        Enum.GetValues<RequestKind>().Select(_ => new OperationMetrics(LatencyUpperBounds.Length)).ToArray();

    public void RecordSent(RequestKind kind) => _metrics[(int)kind].RecordSent();

    public void RecordCompleted(
        RequestKind kind,
        int statusCode,
        double? requestCharge,
        TimeSpan elapsed) =>
        RecordCompleted(kind, statusCode, requestCharge, CosmosQueryScope.Unknown, elapsed);

    public void RecordCompleted(
        RequestKind kind,
        int statusCode,
        double? requestCharge,
        CosmosQueryScope queryScope,
        TimeSpan elapsed) =>
        _metrics[(int)kind].RecordCompleted(statusCode, requestCharge, queryScope, elapsed, LatencyUpperBounds);

    public void RecordNetworkFailure(RequestKind kind, TimeSpan elapsed) =>
        _metrics[(int)kind].RecordNetworkFailure(elapsed, LatencyUpperBounds);

    public MetricsSnapshot Snapshot() => new(_metrics.Select(metric => metric.Snapshot()).ToArray());

    public void Reset()
    {
        foreach (var metric in _metrics)
        {
            metric.Reset();
        }
    }

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

    private static double[] BuildLatencyUpperBounds()
    {
        var bounds = new List<double>();
        for (var milliseconds = 1d; milliseconds < 600_000; milliseconds *= 1.05)
        {
            bounds.Add(Math.Ceiling(milliseconds));
        }

        bounds.Add(600_000);
        bounds.Add(double.PositiveInfinity);
        return bounds.Distinct().ToArray();
    }
}

internal sealed class OperationMetrics(int histogramSize)
{
    private readonly object _sync = new();
    private readonly long[] _histogram = new long[histogramSize];
    private long _sent;
    private long _active;
    private long _completed;
    private long _success;
    private long _throttled;
    private long _clientErrors;
    private long _serverErrors;
    private long _networkErrors;
    private long _charged;
    private double _requestCharge;
    private double _totalMilliseconds;
    private CosmosQueryScope _queryScope;

    public void RecordSent()
    {
        lock (_sync)
        {
            _sent++;
            _active++;
        }
    }

    public void RecordCompleted(
        int statusCode,
        double? requestCharge,
        CosmosQueryScope queryScope,
        TimeSpan elapsed,
        IReadOnlyList<double> latencyUpperBounds)
    {
        lock (_sync)
        {
            _active--;
            _completed++;
            if (statusCode is >= 200 and < 300)
            {
                _success++;
            }
            else if (statusCode == 429)
            {
                _throttled++;
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

            _queryScope = _queryScope.Combine(queryScope);

            RecordLatency(elapsed, latencyUpperBounds);
        }
    }

    public void RecordNetworkFailure(TimeSpan elapsed, IReadOnlyList<double> latencyUpperBounds)
    {
        lock (_sync)
        {
            _active--;
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
                _active,
                _completed,
                _success,
                _throttled,
                _clientErrors,
                _serverErrors,
                _networkErrors,
                _charged,
                _requestCharge,
                _totalMilliseconds,
                _queryScope,
                [.. _histogram]);
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _sent = _active;
            _completed = 0;
            _success = 0;
            _throttled = 0;
            _clientErrors = 0;
            _serverErrors = 0;
            _networkErrors = 0;
            _charged = 0;
            _requestCharge = 0;
            _totalMilliseconds = 0;
            _queryScope = CosmosQueryScope.Unknown;
            Array.Clear(_histogram);
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
    long Active,
    long Completed,
    long Success,
    long Throttled,
    long ClientErrors,
    long ServerErrors,
    long NetworkErrors,
    long Charged,
    double RequestCharge,
    double TotalMilliseconds,
    CosmosQueryScope QueryScope,
    long[] Histogram)
{
    public static OperationMetricsSnapshot operator -(
        OperationMetricsSnapshot current,
        OperationMetricsSnapshot previous) => new(
            current.Sent - previous.Sent,
            current.Active,
            current.Completed - previous.Completed,
            current.Success - previous.Success,
            current.Throttled - previous.Throttled,
            current.ClientErrors - previous.ClientErrors,
            current.ServerErrors - previous.ServerErrors,
            current.NetworkErrors - previous.NetworkErrors,
            current.Charged - previous.Charged,
            current.RequestCharge - previous.RequestCharge,
            current.TotalMilliseconds - previous.TotalMilliseconds,
            current.QueryScope,
            current.Histogram.Zip(previous.Histogram, (left, right) => left - right).ToArray());
}

internal enum CosmosQueryScope
{
    Unknown,
    PointRead,
    SinglePartition,
    CrossPartition,
    NotApplicable,
    Mixed
}

internal static class CosmosQueryScopeExtensions
{
    public static CosmosQueryScope Parse(string? value) => value?.ToLowerInvariant() switch
    {
        "point-read" => CosmosQueryScope.PointRead,
        "single-partition" => CosmosQueryScope.SinglePartition,
        "cross-partition" => CosmosQueryScope.CrossPartition,
        "not-applicable" => CosmosQueryScope.NotApplicable,
        _ => CosmosQueryScope.Unknown
    };

    public static CosmosQueryScope Combine(this CosmosQueryScope current, CosmosQueryScope observed)
    {
        if (observed == CosmosQueryScope.Unknown)
        {
            return current;
        }

        if (current == CosmosQueryScope.Unknown)
        {
            return observed;
        }

        return current == observed ? current : CosmosQueryScope.Mixed;
    }

    public static string ToDisplayName(this CosmosQueryScope scope) => scope switch
    {
        CosmosQueryScope.PointRead => "POINT",
        CosmosQueryScope.SinglePartition => "1PK",
        CosmosQueryScope.CrossPartition => "XPK",
        CosmosQueryScope.NotApplicable => "N/A",
        CosmosQueryScope.Mixed => "MIXED",
        _ => "?"
    };
}

internal sealed record MetricsSnapshot(OperationMetricsSnapshot[] Operations)
{
    public OperationMetricsSnapshot this[RequestKind kind] => Operations[(int)kind];

    public MetricsTotal Total => new(
        Operations.Sum(value => value.Sent),
        Operations.Sum(value => value.Active),
        Operations.Sum(value => value.Completed),
        Operations.Sum(value => value.Success),
        Operations.Sum(value => value.Throttled),
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
    long Active,
    long Completed,
    long Success,
    long Throttled,
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
    private readonly TextWriter _output;
    private readonly Func<int> _widthProvider;
    private bool _interactive;
    private bool _started;
    private bool _completed;

    public LiveDashboard(
        bool? interactive = null,
        TextWriter? output = null,
        Func<int>? widthProvider = null)
    {
        _interactive = interactive ?? !Console.IsOutputRedirected;
        _output = output ?? Console.Out;
        _widthProvider = widthProvider ?? (() => Console.WindowWidth);
    }

    public int Width
    {
        get
        {
            if (!_interactive)
            {
                return int.MaxValue;
            }

            try
            {
                return _widthProvider();
            }
            catch (IOException)
            {
                return int.MaxValue;
            }
        }
    }

    public bool IsInteractive => _interactive && !Console.IsInputRedirected;

    public bool TryReadKey(out ConsoleKeyInfo key)
    {
        key = default;
        if (!IsInteractive)
        {
            return false;
        }

        try
        {
            if (!Console.KeyAvailable)
            {
                return false;
            }

            key = Console.ReadKey(intercept: true);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public void Render(IReadOnlyList<string> lines)
    {
        if (!_interactive)
        {
            foreach (var line in lines)
            {
                _output.WriteLine(line);
            }
            return;
        }

        try
        {
            if (!_started)
            {
                // Use the terminal's alternate screen buffer, like top/htop. The user's normal
                // scrollback is restored when Complete is called.
                _output.Write("\u001b[?1049h\u001b[2J\u001b[H\u001b[?25l");
                _started = true;
            }

            _output.Write("\u001b[2J\u001b[H");
            for (var index = 0; index < lines.Count; index++)
            {
                _output.WriteLine(StyleLine(lines[index], index));
            }
            _output.Flush();
        }
        catch (IOException)
        {
            _interactive = false;
            RestoreTerminal();
            _output.WriteLine();
            foreach (var line in lines)
            {
                _output.WriteLine(line);
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

    private static string StyleLine(string line, int index)
    {
        const string reset = "\u001b[0m";
        if (index == 0)
        {
            return $"\u001b[1;36m{line}{reset}";
        }

        if (index == 1 || line.StartsWith("operation", StringComparison.Ordinal))
        {
            return $"\u001b[36m{line}{reset}";
        }

        if (line.Contains("PAUSED", StringComparison.Ordinal) || HasPositiveMetricColumn(line, 4))
        {
            return $"\u001b[33m{line}{reset}";
        }

        if (HasPositiveMetricColumn(line, 5))
        {
            return $"\u001b[31m{line}{reset}";
        }

        return $"\u001b[32m{line}{reset}";
    }

    private static bool HasPositiveMetricColumn(string line, int offsetFromScope)
    {
        var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var scopeIndex = Array.FindIndex(columns, column => column is "POINT" or "1PK" or "XPK" or "N/A" or "MIXED" or "?");
        var metricIndex = scopeIndex + offsetFromScope;
        return scopeIndex >= 0 && columns.Length > metricIndex &&
            long.TryParse(
                columns[metricIndex],
                NumberStyles.Integer | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out var value) &&
            value > 0;
    }

    private void RestoreTerminal()
    {
        if (!_started)
        {
            return;
        }
        try
        {
            _output.Write("\u001b[?25h\u001b[?1049l");
            _output.Flush();
            _started = false;
        }
        catch (IOException)
        {
            _started = false;
        }
    }
}
