using System.Diagnostics;
using System.Text;
using System.Text.Json;
using static LoadGenConstants;

// Standalone traffic generator for the ticketing API. It floods the API and reads back the
// Cosmos DB RU charge header, keeping a live in-place console readout. Nothing is written to
// disk and no analysis happens beyond the final totals printed on exit.
// Usage: LoadGen --concurrency <n> [--seed <n>] [--duration <seconds>] [--base-url <url>]

var parsed = ParseArgs(args);
if (parsed is null)
{
    Console.Error.WriteLine("Usage: LoadGen --concurrency <n> [--seed <n>] [--duration <seconds>] [--base-url <url>]");
    return 1;
}

var (requestedSeed, baseConcurrency, durationSeconds, baseUrl) = parsed.Value;
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

var baseWeightTotal = baseWeights.Sum(w => w.Weight);
var burstWeightTotal = burstWeights.Sum(w => w.Weight);

using var http = new HttpClient(new SocketsHttpHandler
{
    MaxConnectionsPerServer = 4_000,
    PooledConnectionLifetime = TimeSpan.FromMinutes(5)
})
{
    BaseAddress = new Uri(baseUrl),
    Timeout = TimeSpan.FromSeconds(30)
};

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
await DiscoverRoutesAsync(http, routes);

var rng = new Random(seed);
var stopwatch = Stopwatch.StartNew();

var totalSent = 0L;
var completedWithCharge = 0L;
var totalRuCharge = 0.0;
var ruLock = new object();
var kindCounts = new long[Enum.GetValues<RequestKind>().Length];
var kindRuCharges = new double[Enum.GetValues<RequestKind>().Length];

// Ctrl+C should stop launching new requests and drain in-flight ones, not kill the process outright.
using var stopRequested = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stopRequested.Cancel();
};

var inFlight = new List<Task>();
var lastPrintElapsed = TimeSpan.Zero;
var lastPrintSent = 0L;
var lastPrintRu = 0.0;

Console.WriteLine($"loadgen: seed={seed} concurrency={baseConcurrency} duration={(openEnded ? "until Ctrl+C" : $"{durationSeconds}s")} target={baseUrl}");

while (!stopRequested.IsCancellationRequested && (openEnded || stopwatch.Elapsed < duration))
{
    inFlight.RemoveAll(t => t.IsCompleted);

    var elapsed = stopwatch.Elapsed;
    var cyclePosition = elapsed.TotalSeconds % BurstPeriodSeconds;
    var inBurst = cyclePosition < BurstDurationSeconds;
    var targetConcurrency = inBurst ? baseConcurrency * BurstMultiplier : baseConcurrency;

    while (inFlight.Count < targetConcurrency && !stopRequested.IsCancellationRequested &&
           (openEnded || stopwatch.Elapsed < duration))
    {
        var (kind, method, path, body) = NextRequest(rng, inBurst);
        inFlight.Add(SendAsync(http, kind, method, path, body));
        Interlocked.Increment(ref totalSent);
    }

    if ((elapsed - lastPrintElapsed).TotalSeconds >= 0.2)
    {
        var sent = Interlocked.Read(ref totalSent);
        double ru;
        lock (ruLock) { ru = totalRuCharge; }

        var intervalSeconds = (elapsed - lastPrintElapsed).TotalSeconds;
        var rate = (sent - lastPrintSent) / intervalSeconds;
        var ruPerSec = (ru - lastPrintRu) / intervalSeconds;

        // \r overwrites the same line so this stays readable instead of scrolling.
        Console.Write($"\rsent={sent}  rate={rate,7:F1}/s  RU/s={ruPerSec,8:F1}  totalRU={ru,10:F1}   ");
        lastPrintElapsed = elapsed;
        lastPrintSent = sent;
        lastPrintRu = ru;
    }

    await Task.Delay(20);
}

Console.Write($"\rstopping - draining {inFlight.Count} in-flight request(s)...                                   \n");
await Task.WhenAll(inFlight);

var finalSent = Interlocked.Read(ref totalSent);
double finalRu;
lock (ruLock) { finalRu = totalRuCharge; }
var finalCompleted = Interlocked.Read(ref completedWithCharge);
var avgRu = finalCompleted > 0 ? finalRu / finalCompleted : 0.0;

Console.WriteLine($"loadgen: total requests={finalSent}  total RU={finalRu:F1}  avg RU/request={avgRu:F2}");
Console.WriteLine("RU by endpoint:");
lock (ruLock)
{
    foreach (var kind in Enum.GetValues<RequestKind>())
    {
        var count = kindCounts[(int)kind];
        var ru = kindRuCharges[(int)kind];
        var avg = count > 0 ? ru / count : 0.0;
        Console.WriteLine($"  {kind,-16} requests={count,-7} totalRU={ru,10:F1}  avgRU={avg,6:F2}");
    }
}
return 0;

(RequestKind Kind, HttpMethod Method, string Path, string? Body) NextRequest(Random r, bool burst)
{
    var weights = burst ? burstWeights : baseWeights;
    var total = burst ? burstWeightTotal : baseWeightTotal;
    var kind = PickKind(r, weights, total);

    var (method, path, body) = kind switch
    {
        RequestKind.EventDetail =>
            (HttpMethod.Get, BuildPath(routes[kind], PickEventId(r)), (string?)null),

        RequestKind.UpcomingEvents =>
            (HttpMethod.Get, routes[kind], null),

        RequestKind.EventsByCity =>
            (HttpMethod.Get, BuildPath(routes[kind], cities[r.Next(cities.Length)]), null),

        RequestKind.PurchaseTicket =>
            (HttpMethod.Post, routes[kind], JsonSerializer.Serialize(new
            {
                eventId = PickEventId(r),
                customerId = PickCustomerId(r),
                quantity = r.Next(1, 5)
            })),

        RequestKind.OrdersByCustomer =>
            (HttpMethod.Get, BuildPath(routes[kind], PickCustomerId(r)), null),

        RequestKind.OrdersByEvent =>
            (HttpMethod.Get, BuildPath(routes[kind], PickEventId(r)), null),

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

// Best-effort: reads the API's own OpenAPI doc and reconciles our known routes against it, so a
// renamed/moved endpoint is picked up automatically instead of silently 404ing all night on stream.
async Task DiscoverRoutesAsync(HttpClient client, Dictionary<RequestKind, string> targetRoutes)
{
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var response = await client.GetAsync("/openapi/v1.json", cts.Token);
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine("loadgen: OpenAPI doc unavailable, using built-in default routes");
            return;
        }

        using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);

        if (!doc.RootElement.TryGetProperty("paths", out var paths))
        {
            Console.WriteLine("loadgen: OpenAPI doc had no paths, using built-in default routes");
            return;
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
    }
    catch (Exception ex)
    {
        // Discovery is a nicety, not a requirement - never let it block the run.
        Console.WriteLine($"loadgen: OpenAPI discovery failed ({ex.GetType().Name}), using built-in default routes");
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
    try
    {
        using var request = new HttpRequestMessage(method, path);
        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        if (response.Headers.TryGetValues("x-ms-request-charge", out var values) &&
            double.TryParse(values.FirstOrDefault(), out var charge))
        {
            lock (ruLock)
            {
                totalRuCharge += charge;
                kindRuCharges[(int)kind] += charge;
                kindCounts[(int)kind]++;
            }
            Interlocked.Increment(ref completedWithCharge);
        }

        await response.Content.CopyToAsync(Stream.Null);
    }
    catch
    {
        // This is a traffic generator, not a test harness - failed/timed-out requests are dropped silently.
    }
}

(int? Seed, int Concurrency, double? DurationSeconds, string BaseUrl)? ParseArgs(string[] a)
{
    int? seed = null;
    int? concurrency = null;
    double? parsedDuration = null;
    var url = "http://localhost:5107";

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
            case "--duration" when i + 1 < a.Length && double.TryParse(a[i + 1], out var dur):
                parsedDuration = dur;
                i++;
                break;
            case "--base-url" when i + 1 < a.Length:
                url = a[i + 1];
                i++;
                break;
            default:
                return null;
        }
    }

    return concurrency is null ? null : (seed, concurrency.Value, parsedDuration, url);
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

internal static class LoadGenConstants
{
    public const string ChampionshipEventId = "event-00001";
    public const int EventCount = 5_000;
    public const int CustomerCount = 25_000;
    public const double ChampionshipShare = 0.75;
    public const int BurstPeriodSeconds = 30;
    public const int BurstDurationSeconds = 5;
    public const int BurstMultiplier = 10;
}
