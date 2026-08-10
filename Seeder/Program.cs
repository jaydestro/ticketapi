using System.Diagnostics;
using Azure.Identity;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Seeder;
using Seeder.Models;

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables()
    .Build();

var options = configuration.GetSection("CosmosDb").Get<CosmosDbOptions>()
    ?? throw new InvalidOperationException("Missing CosmosDb configuration section.");

var clientOptions = new CosmosClientOptions
{
    AllowBulkExecution = true,
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
    Console.WriteLine("Ensuring database/containers exist...");
    // Note: no explicit throughput is requested here so this works against both a serverless
    // Azure Cosmos DB account (which rejects an explicit throughput) and the local emulator.
    Database database = await client.CreateDatabaseIfNotExistsAsync(options.DatabaseName);
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

    Console.WriteLine();
    Console.WriteLine("=== Seed complete ===");
    Console.WriteLine($"Documents written: {progress.DocumentCount:N0}");
    Console.WriteLine($"Total RU consumed: {progress.TotalRequestCharge:N2}");
    Console.WriteLine($"Wall-clock time:   {stopwatch.Elapsed}");
}

return 0;

// Upserts items concurrently in chunks, relying on CosmosClientOptions.AllowBulkExecution
// to batch requests efficiently. Upsert (not Create) makes re-runs idempotent.
static async Task BulkUpsertAsync<T>(
    Container container,
    IEnumerable<T> items,
    Func<T, PartitionKey> partitionKeySelector,
    SeedProgress progress,
    int chunkSize = 1000)
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
    try
    {
        var response = await container.UpsertItemAsync(item, partitionKey);
        progress.Record(response.RequestCharge);
    }
    catch (CosmosException ex)
    {
        Console.WriteLine($"Failed to upsert item ({typeof(T).Name}): {ex.StatusCode} - {ex.Message}");
    }
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
