using System.Diagnostics;
using Azure.Identity;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Seeder;
using Seeder.Models;

var target = "legacy";
var reset = false;
var projectionsOnly = false;
for (var index = 0; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--target" when index + 1 < args.Length && args[index + 1] is "legacy" or "after":
            target = args[++index];
            break;
        case "--reset":
            reset = true;
            break;
        case "--projections-only":
            projectionsOnly = true;
            break;
        default:
            Console.Error.WriteLine("Usage: Seeder [--target legacy|after] [--reset] [--projections-only]");
            return 1;
    }
}

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables()
    .Build();

var options = configuration.GetSection("CosmosDb").Get<CosmosDbOptions>()
    ?? throw new InvalidOperationException("Missing CosmosDb configuration section.");

if (projectionsOnly && target != "after")
{
    Console.Error.WriteLine("--projections-only requires --target after.");
    return 1;
}

var clientOptions = new CosmosClientOptions
{
    AllowBulkExecution = true,
    MaxRetryAttemptsOnRateLimitedRequests = 100,
    MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.FromMinutes(30),
    // Match the API: honor System.Text.Json attributes like [JsonPropertyName("id")].
    UseSystemTextJsonSerializerWithOptions = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
};

CosmosClient client;

// Live Azure Cosmos DB: authenticate with Microsoft Entra ID (no account key, passwordless).
if (!string.IsNullOrWhiteSpace(options.AccountEndpoint))
{
    client = new CosmosClient(options.AccountEndpoint, new DefaultAzureCredential(), clientOptions);
}
else
{
    if (options.ConnectionString.Contains("localhost", StringComparison.OrdinalIgnoreCase))
    {
        // Local Cosmos DB Emulator uses a self-signed cert; trust it only for this local endpoint.
        clientOptions.HttpClientFactory = () => new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        });
        clientOptions.ConnectionMode = ConnectionMode.Gateway;
    }

    client = new CosmosClient(options.ConnectionString, clientOptions);
}

using (client)
{
    Console.WriteLine($"Ensuring database/containers exist for target '{target}'...");
    // Note: no explicit throughput is requested here so this works against both a serverless
    // Azure Cosmos DB account (which rejects an explicit throughput) and the local emulator.
    Database database = await client.CreateDatabaseIfNotExistsAsync(options.DatabaseName);
    if (target == "after")
    {
        return await SeedAfterAsync(database, options, reset, projectionsOnly);
    }

    Container eventsContainer = (await database.CreateContainerIfNotExistsAsync(new ContainerProperties
    {
        Id = options.EventsContainerName,
        PartitionKeyPath = "/id"
    })).Container;
    Container ordersContainer = (await database.CreateContainerIfNotExistsAsync(new ContainerProperties
    {
        Id = options.OrdersContainerName,
        PartitionKeyPath = "/id"
    })).Container;

    var random = new Random(42); // fixed seed => deterministic, idempotent data across re-runs

    Console.WriteLine($"Generating {DataGenerator.EventCount:N0} events...");
    var events = DataGenerator.GenerateEvents(random);

    Console.WriteLine($"Computing skewed order distribution across {DataGenerator.EventCount:N0} events...");
    var orderCounts = DataGenerator.ComputeOrderDistribution(random);

    var stopwatch = Stopwatch.StartNew();
    var progress = new SeedProgress();

    Console.WriteLine($"Seeding {events.Count:N0} events...");
    await BulkUpsertAsync(eventsContainer, events, e => new PartitionKey(e.Id), progress);

    Console.WriteLine($"Generating and seeding {DataGenerator.OrderCount:N0} orders (this is the bulk of the work)...");
    var orders = DataGenerator.GenerateOrders(events, orderCounts, random);
    await BulkUpsertAsync(ordersContainer, orders, o => new PartitionKey(o.Id), progress);

    stopwatch.Stop();

    PrintSummary(progress, stopwatch.Elapsed);
}

return 0;

static async Task<int> SeedAfterAsync(
    Database database,
    CosmosDbOptions options,
    bool reset,
    bool projectionsOnly)
{
    if (reset)
    {
        foreach (var containerName in new[]
        {
            options.TicketingContainerName,
            options.EventsByCityContainerName,
            options.OrdersByCustomerContainerName,
            options.LeaseContainerName
        })
        {
            await DeleteContainerIfExistsAsync(database, containerName);
        }
    }

    var writeContainer = (await database.CreateContainerIfNotExistsAsync(
        CreateIndexedContainer(
            options.TicketingContainerName,
            "/eventId",
            ["/eventId/?", "/type/?", "/orderDate/?"]))).Container;
    var eventsByCity = (await database.CreateContainerIfNotExistsAsync(
        CreateIndexedContainer(
            options.EventsByCityContainerName,
            "/cityKey",
            ["/cityKey/?", "/eventDate/?"]))).Container;
    var ordersByCustomer = (await database.CreateContainerIfNotExistsAsync(
        CreateIndexedContainer(
            options.OrdersByCustomerContainerName,
            "/customerId",
            ["/customerId/?", "/orderDate/?"]))).Container;
    await database.CreateContainerIfNotExistsAsync(
        new ContainerProperties(options.LeaseContainerName, "/id"));

    var random = new Random(42);
    Console.WriteLine($"Generating {DataGenerator.EventCount:N0} events...");
    var events = DataGenerator.GenerateEvents(random);
    Console.WriteLine($"Computing skewed order distribution across {DataGenerator.EventCount:N0} events...");
    var orderCounts = DataGenerator.ComputeOrderDistribution(random);
    var stopwatch = Stopwatch.StartNew();
    var progress = new SeedProgress();

    if (!projectionsOnly)
    {
        Console.WriteLine($"Seeding {events.Count:N0} event source documents...");
        await BulkUpsertAsync(
            writeContainer,
            events.Select(TicketEventDocument.FromModel),
            document => new PartitionKey(document.EventId),
            progress);
    }

    Console.WriteLine($"Seeding {events.Count:N0} event-by-city projection documents...");
    await BulkUpsertAsync(
        eventsByCity,
        events.Select(EventByCityDocument.FromModel),
        document => new PartitionKey(document.CityKey),
        progress);

    Console.WriteLine($"Generating {DataGenerator.OrderCount:N0} orders...");
    var orders = DataGenerator.GenerateOrders(events, orderCounts, random).ToArray();
    if (!projectionsOnly)
    {
        Console.WriteLine($"Seeding {orders.Length:N0} order source documents...");
        await BulkUpsertAsync(
            writeContainer,
            orders.Select(OrderDocument.FromModel),
            document => new PartitionKey(document.EventId),
            progress);
    }

    Console.WriteLine($"Seeding {orders.Length:N0} order-by-customer projection documents...");
    await BulkUpsertAsync(
        ordersByCustomer,
        orders.Select(OrderByCustomerDocument.FromModel),
        document => new PartitionKey(document.CustomerId),
        progress);

    var marker = new ReadModelBackfillMarker();
    await UpsertOneAsync(eventsByCity, marker, new PartitionKey(marker.CityKey), progress);

    stopwatch.Stop();
    PrintSummary(progress, stopwatch.Elapsed);
    return 0;
}

// Upserts items concurrently in chunks, relying on CosmosClientOptions.AllowBulkExecution
// to batch requests efficiently. Upsert (not Create) makes re-runs idempotent.
static async Task BulkUpsertAsync<T>(
    Container container,
    IEnumerable<T> items,
    Func<T, PartitionKey> partitionKeySelector,
    SeedProgress progress,
    int chunkSize = 200)
{
    var chunk = new List<T>(chunkSize);

    foreach (var item in items)
    {
        chunk.Add(item);
        if (chunk.Count >= chunkSize)
        {
            await ProcessChunkAsync(container, chunk, partitionKeySelector, progress);
            chunk.Clear();
        }
    }

    if (chunk.Count > 0)
    {
        await ProcessChunkAsync(container, chunk, partitionKeySelector, progress);
    }
}

static async Task ProcessChunkAsync<T>(
    Container container,
    List<T> chunk,
    Func<T, PartitionKey> partitionKeySelector,
    SeedProgress progress)
{
    var tasks = chunk.Select(item => UpsertOneAsync(container, item, partitionKeySelector(item), progress));
    await Task.WhenAll(tasks);
}

static async Task UpsertOneAsync<T>(Container container, T item, PartitionKey partitionKey, SeedProgress progress)
{
    var response = await container.UpsertItemAsync(item, partitionKey);
    progress.Record(response.RequestCharge);
}

static async Task DeleteContainerIfExistsAsync(Database database, string containerName)
{
    try
    {
        Console.WriteLine($"Deleting existing container {containerName}...");
        await database.GetContainer(containerName).DeleteContainerAsync();
    }
    catch (CosmosException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        Console.WriteLine($"Container {containerName} does not exist; continuing.");
    }
}

static ContainerProperties CreateIndexedContainer(
    string name,
    string partitionKeyPath,
    IReadOnlyCollection<string> includedPaths)
{
    var properties = new ContainerProperties(name, partitionKeyPath)
    {
        IndexingPolicy = new IndexingPolicy
        {
            Automatic = true,
            IndexingMode = IndexingMode.Consistent
        }
    };
    properties.IndexingPolicy.IncludedPaths.Clear();
    foreach (var path in includedPaths)
    {
        properties.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = path });
    }
    properties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/*" });
    properties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/\"_etag\"/?" });
    return properties;
}

static void PrintSummary(SeedProgress progress, TimeSpan elapsed)
{
    Console.WriteLine();
    Console.WriteLine("=== Seed complete ===");
    Console.WriteLine($"Documents written: {progress.DocumentCount:N0}");
    Console.WriteLine($"Total RU consumed: {progress.TotalRequestCharge:N2}");
    Console.WriteLine($"Wall-clock time:   {elapsed}");
}

// Tracks running totals across concurrent bulk writes and prints progress every 10,000 documents.
internal sealed class SeedProgress
{
    private long _documentCount;
    private readonly object _ruLock = new();
    private double _totalRequestCharge;

    public long DocumentCount => _documentCount;
    public double TotalRequestCharge => _totalRequestCharge;

    public void Record(double requestCharge)
    {
        lock (_ruLock)
        {
            _totalRequestCharge += requestCharge;
        }

        var count = Interlocked.Increment(ref _documentCount);
        if (count % 10_000 == 0)
        {
            Console.WriteLine($"  ...{count:N0} documents written (running RU total: {_totalRequestCharge:N0})");
        }
    }
}
