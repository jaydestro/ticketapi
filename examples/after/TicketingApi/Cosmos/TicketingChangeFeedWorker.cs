using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using TicketingApi.Configuration;

namespace TicketingApi.Cosmos;

public sealed class TicketingChangeFeedWorker(
    CosmosClient client,
    IOptions<CosmosDbOptions> options,
    CosmosReadinessState readiness,
    ILogger<TicketingChangeFeedWorker> logger) : IHostedService
{
    private const string BackfillMarkerId = "ticketing-read-models-v1-backfill";
    private const string SystemPartitionKey = "__SYSTEM__";
    private ChangeFeedProcessor? _processor;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var value = options.Value;
        var database = client.GetDatabase(value.DatabaseName);
        var source = database.GetContainer(value.TicketingContainerName);
        var eventsByCity = database.GetContainer(value.EventsByCityContainerName);
        var ordersByCustomer = database.GetContainer(value.OrdersByCustomerContainerName);
        var leases = database.GetContainer(value.LeaseContainerName);
        var processorStartTime = DateTime.UtcNow;

        if (!await IsBackfillCompleteAsync(eventsByCity, cancellationToken))
        {
            await BackfillAsync(source, eventsByCity, ordersByCustomer, cancellationToken);
            await WriteBackfillMarkerAsync(eventsByCity, cancellationToken);
        }
        else
        {
            logger.LogInformation("Cosmos read-model backfill is already complete");
        }

        _processor = source
            .GetChangeFeedProcessorBuilder<JsonElement>(
                value.ChangeFeedProcessorName,
                async (changes, token) =>
                {
                    foreach (var change in changes)
                    {
                        await UpsertProjectionAsync(change, eventsByCity, ordersByCustomer, token);
                    }
                })
            .WithInstanceName($"{Environment.MachineName}-{Environment.ProcessId}")
            .WithLeaseContainer(leases)
            .WithStartTime(processorStartTime)
            .Build();

        try
        {
            await _processor.StartAsync();
            readiness.ChangeFeedReady = true;
            logger.LogInformation("Change feed processor {ProcessorName} started", value.ChangeFeedProcessorName);
        }
        catch (Exception exception)
        {
            readiness.Failure = exception.Message;
            logger.LogCritical(exception, "Change feed processor failed to start");
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        readiness.ChangeFeedReady = false;
        if (_processor is not null)
        {
            await _processor.StopAsync();
        }
    }

    private async Task BackfillAsync(
        Container source,
        Container eventsByCity,
        Container ordersByCustomer,
        CancellationToken cancellationToken)
    {
        var count = 0;
        using var iterator = source.GetItemQueryIterator<JsonElement>(
            requestOptions: new QueryRequestOptions { MaxItemCount = 100 });
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(cancellationToken);
            LogResponse("BackfillRead", page.RequestCharge, page.Diagnostics);
            await Task.WhenAll(page.Select(change =>
                UpsertProjectionAsync(change, eventsByCity, ordersByCustomer, cancellationToken)));
            count += page.Count;
            if (count % 10_000 == 0)
            {
                logger.LogInformation("Backfilled {DocumentCount} Cosmos read-model documents", count);
            }
        }

        logger.LogInformation("Backfilled {DocumentCount} Cosmos read-model documents", count);
    }

    private static async Task<bool> IsBackfillCompleteAsync(
        Container eventsByCity,
        CancellationToken cancellationToken)
    {
        try
        {
            await eventsByCity.ReadItemAsync<ReadModelBackfillMarker>(
                BackfillMarkerId,
                new PartitionKey(SystemPartitionKey),
                cancellationToken: cancellationToken);
            return true;
        }
        catch (CosmosException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static async Task WriteBackfillMarkerAsync(
        Container eventsByCity,
        CancellationToken cancellationToken)
    {
        var marker = new ReadModelBackfillMarker
        {
            Id = BackfillMarkerId,
            CityKey = SystemPartitionKey,
            CompletedAt = DateTime.UtcNow
        };
        await eventsByCity.UpsertItemAsync(
            marker,
            new PartitionKey(marker.CityKey),
            cancellationToken: cancellationToken);
    }

    private async Task UpsertProjectionAsync(
        JsonElement change,
        Container eventsByCity,
        Container ordersByCustomer,
        CancellationToken cancellationToken)
    {
        if (!change.TryGetProperty("type", out var typeProperty))
        {
            logger.LogWarning("Skipping Cosmos change without a type discriminator: {Change}", change);
            return;
        }

        switch (typeProperty.GetString())
        {
            case TicketingDocumentTypes.Event:
            {
                var eventDocument = change.Deserialize<TicketEventDocument>(SerializerOptions)
                    ?? throw new JsonException("Could not deserialize event change.");
                var projection = EventByCityDocument.FromSource(eventDocument);
                var response = await eventsByCity.UpsertItemAsync(
                    projection,
                    new PartitionKey(projection.CityKey),
                    cancellationToken: cancellationToken);
                LogResponse("ProjectEventByCity", response.RequestCharge, response.Diagnostics);
                break;
            }
            case TicketingDocumentTypes.Order:
            {
                var orderDocument = change.Deserialize<OrderDocument>(SerializerOptions)
                    ?? throw new JsonException("Could not deserialize order change.");
                var projection = OrderByCustomerDocument.FromSource(orderDocument);
                var response = await ordersByCustomer.UpsertItemAsync(
                    projection,
                    new PartitionKey(projection.CustomerId),
                    cancellationToken: cancellationToken);
                LogResponse("ProjectOrderByCustomer", response.RequestCharge, response.Diagnostics);
                break;
            }
            default:
                logger.LogWarning("Skipping Cosmos change with unknown type {Type}", typeProperty.GetString());
                break;
        }
    }

    private void LogResponse(string operation, double requestCharge, CosmosDiagnostics diagnostics) =>
        logger.LogDebug(
            "Cosmos {Operation} used {RequestCharge} RU in {ElapsedMs} ms",
            operation,
            requestCharge,
            diagnostics.GetClientElapsedTime().TotalMilliseconds);

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private sealed class ReadModelBackfillMarker
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;
        public string CityKey { get; init; } = string.Empty;
        public string Type { get; init; } = "metadata";
        public DateTime CompletedAt { get; init; }
    }
}
