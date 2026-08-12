using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using TicketingApi.Configuration;
using TicketingApi.Cosmos;
using TicketingApi.Models;
using TicketingApi.Pagination;

namespace TicketingApi.Repositories;

public sealed class TicketingRepository : ITicketingRepository
{
    private const int MaximumConcurrencyRetries = 4;
    private readonly Container _write;
    private readonly Container _eventsByCity;
    private readonly Container _ordersByCustomer;
    private readonly ILogger<TicketingRepository> _logger;
    private readonly int _slowThresholdMilliseconds;

    public TicketingRepository(
        CosmosClient client,
        IOptions<CosmosDbOptions> options,
        ILogger<TicketingRepository> logger)
    {
        var value = options.Value;
        var database = client.GetDatabase(value.DatabaseName);
        _write = database.GetContainer(value.TicketingContainerName);
        _eventsByCity = database.GetContainer(value.EventsByCityContainerName);
        _ordersByCustomer = database.GetContainer(value.OrdersByCustomerContainerName);
        _logger = logger;
        _slowThresholdMilliseconds = value.SlowOperationThresholdMilliseconds;
    }

    public async Task<CosmosResult<TicketEvent>> CreateEventAsync(TicketEvent ticketEvent, CancellationToken cancellationToken)
    {
        var document = TicketEventDocument.FromModel(ticketEvent);
        using var response = await _write.CreateTransactionalBatch(new PartitionKey(document.EventId))
            .CreateItem(document)
            .ExecuteAsync(cancellationToken);
        LogBatch("CreateEvent", response);
        ThrowIfFailed(response, "CreateEvent");
        return new CosmosResult<TicketEvent>(ticketEvent, response.RequestCharge, CosmosQueryScopes.NotApplicable);
    }

    public async Task<CosmosResult<TicketEvent?>> GetEventAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _write.ReadItemAsync<TicketEventDocument>(id, new PartitionKey(id), cancellationToken: cancellationToken);
            LogResponse("GetEvent", response.RequestCharge, response.Diagnostics);
            return new CosmosResult<TicketEvent?>(response.Resource.ToModel(), response.RequestCharge, CosmosQueryScopes.PointRead);
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return new CosmosResult<TicketEvent?>(null, exception.RequestCharge, CosmosQueryScopes.PointRead);
        }
        catch (CosmosException exception)
        {
            exception.Data[CosmosQueryScopes.ExceptionDataKey] = CosmosQueryScopes.PointRead;
            throw;
        }
    }

    public Task<CosmosPage<TicketEvent>> GetUpcomingEventsAsync(int pageSize, string? continuationToken, CancellationToken cancellationToken)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.eventDate >= @now ORDER BY c.eventDate")
            .WithParameter("@now", DateTime.UtcNow);
        return QueryPageAsync<EventByCityDocument, TicketEvent>(
            _eventsByCity, query, pageSize, continuationToken, null,
            document => document.ToModel(), "GetUpcomingEvents", cancellationToken);
    }

    public Task<CosmosPage<TicketEvent>> GetEventsByCityAsync(string city, int pageSize, string? continuationToken, CancellationToken cancellationToken)
    {
        var cityKey = EventByCityDocument.NormalizeCity(city);
        return QueryPageAsync<EventByCityDocument, TicketEvent>(
            _eventsByCity, new QueryDefinition("SELECT * FROM c ORDER BY c.eventDate"),
            pageSize, continuationToken, new PartitionKey(cityKey),
            document => document.ToModel(), "GetEventsByCity", cancellationToken);
    }

    public async Task<CosmosResult<Order?>> PurchaseTicketsAsync(
        PurchaseTicketsRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var orderId = CreateOrderId(request.EventId, idempotencyKey);
        var existingOrder = await TryReadOrderAsync(orderId, request.EventId, cancellationToken);
        if (existingOrder is not null)
        {
            EnsureMatchingRequest(existingOrder.Value.Order, request);
            return new CosmosResult<Order?>(existingOrder.Value.Order, existingOrder.Value.RequestCharge, CosmosQueryScopes.PointRead);
        }

        for (var attempt = 0; attempt < MaximumConcurrencyRetries; attempt++)
        {
            ItemResponse<TicketEventDocument> eventResponse;
            try
            {
                eventResponse = await _write.ReadItemAsync<TicketEventDocument>(
                    request.EventId, new PartitionKey(request.EventId), cancellationToken: cancellationToken);
            }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
                return new CosmosResult<Order?>(null, exception.RequestCharge, CosmosQueryScopes.NotApplicable);
            }

            var ticketEvent = eventResponse.Resource;
            if (ticketEvent.AvailableSeats < request.Quantity)
            {
                throw new TicketsUnavailableException("Not enough seats are available.");
            }

            ticketEvent.AvailableSeats -= request.Quantity;
            var order = new OrderDocument
            {
                Id = orderId,
                EventId = request.EventId,
                CustomerId = request.CustomerId,
                Quantity = request.Quantity,
                PriceTier = ticketEvent.PriceTier,
                TotalPrice = GetUnitPrice(ticketEvent.PriceTier) * request.Quantity,
                OrderDate = DateTime.UtcNow
            };

            using var batchResponse = await _write.CreateTransactionalBatch(new PartitionKey(request.EventId))
                .ReplaceItem(ticketEvent.Id, ticketEvent, new TransactionalBatchItemRequestOptions { IfMatchEtag = eventResponse.ETag })
                .CreateItem(order)
                .ExecuteAsync(cancellationToken);
            LogBatch("PurchaseTickets", batchResponse);

            if (batchResponse.IsSuccessStatusCode)
            {
                return new CosmosResult<Order?>(order.ToModel(), batchResponse.RequestCharge, CosmosQueryScopes.NotApplicable);
            }

            if (batchResponse.StatusCode == HttpStatusCode.Conflict)
            {
                var existing = await TryReadOrderAsync(orderId, request.EventId, cancellationToken);
                if (existing is not null)
                {
                    EnsureMatchingRequest(existing.Value.Order, request);
                    return new CosmosResult<Order?>(existing.Value.Order, batchResponse.RequestCharge + existing.Value.RequestCharge, CosmosQueryScopes.NotApplicable);
                }
            }

            if (batchResponse.StatusCode == HttpStatusCode.PreconditionFailed ||
                batchResponse[0].StatusCode == HttpStatusCode.PreconditionFailed)
            {
                existingOrder = await TryReadOrderAsync(orderId, request.EventId, cancellationToken);
                if (existingOrder is not null)
                {
                    EnsureMatchingRequest(existingOrder.Value.Order, request);
                    return new CosmosResult<Order?>(
                        existingOrder.Value.Order,
                        batchResponse.RequestCharge + existingOrder.Value.RequestCharge,
                        CosmosQueryScopes.NotApplicable);
                }

                await Task.Delay(Random.Shared.Next(10, 50), cancellationToken);
                continue;
            }

            ThrowIfFailed(batchResponse, "PurchaseTickets");
        }

        throw new TicketsUnavailableException("Inventory changed too frequently. Retry the purchase.");
    }

    public Task<CosmosPage<Order>> GetOrdersByCustomerAsync(string customerId, int pageSize, string? continuationToken, CancellationToken cancellationToken) =>
        QueryPageAsync<OrderByCustomerDocument, Order>(
            _ordersByCustomer, new QueryDefinition("SELECT * FROM c ORDER BY c.orderDate DESC"),
            pageSize, continuationToken, new PartitionKey(customerId),
            document => document.ToModel(), "GetOrdersByCustomer", cancellationToken);

    public Task<CosmosPage<Order>> GetOrdersByEventAsync(string eventId, int pageSize, string? continuationToken, CancellationToken cancellationToken)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.type = @type ORDER BY c.orderDate DESC")
            .WithParameter("@type", TicketingDocumentTypes.Order);
        return QueryPageAsync<OrderDocument, Order>(
            _write, query, pageSize, continuationToken, new PartitionKey(eventId),
            document => document.ToModel(), "GetOrdersByEvent", cancellationToken);
    }

    private async Task<CosmosPage<TModel>> QueryPageAsync<TDocument, TModel>(
        Container container,
        QueryDefinition query,
        int pageSize,
        string? continuationToken,
        PartitionKey? partitionKey,
        Func<TDocument, TModel> map,
        string operationName,
        CancellationToken cancellationToken)
    {
        var options = new QueryRequestOptions { MaxItemCount = pageSize, PartitionKey = partitionKey };
        using var iterator = container.GetItemQueryIterator<TDocument>(query, continuationToken, options);
        FeedResponse<TDocument> response;
        try
        {
            response = await iterator.ReadNextAsync(cancellationToken);
        }
        catch (CosmosException exception)
        {
            exception.Data[CosmosQueryScopes.ExceptionDataKey] = partitionKey is null
                ? CosmosQueryScopes.CrossPartition
                : CosmosQueryScopes.SinglePartition;
            throw;
        }
        LogResponse(operationName, response.RequestCharge, response.Diagnostics);
        return new CosmosPage<TModel>(
            response.Select(map).ToArray(), response.ContinuationToken, response.RequestCharge,
            partitionKey is null ? CosmosQueryScopes.CrossPartition : CosmosQueryScopes.SinglePartition);
    }

    private async Task<(Order Order, double RequestCharge)?> TryReadOrderAsync(string orderId, string eventId, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _write.ReadItemAsync<OrderDocument>(orderId, new PartitionKey(eventId), cancellationToken: cancellationToken);
            LogResponse("GetOrderByIdempotencyKey", response.RequestCharge, response.Diagnostics);
            return (response.Resource.ToModel(), response.RequestCharge);
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            LogResponse("GetOrderByIdempotencyKey", exception.RequestCharge, exception.Diagnostics);
            return null;
        }
    }

    private void LogResponse(string operation, double requestCharge, CosmosDiagnostics diagnostics)
    {
        var elapsed = diagnostics.GetClientElapsedTime();
        _logger.LogDebug("Cosmos {Operation} used {RequestCharge} RU in {ElapsedMs} ms", operation, requestCharge, elapsed.TotalMilliseconds);
        if (elapsed.TotalMilliseconds >= _slowThresholdMilliseconds || requestCharge >= 10)
        {
            _logger.LogWarning(
                "Slow or expensive Cosmos {Operation}: {RequestCharge} RU, {ElapsedMs} ms, diagnostics={Diagnostics}",
                operation, requestCharge, elapsed.TotalMilliseconds, diagnostics.ToString());
        }
    }

    private void LogBatch(string operation, TransactionalBatchResponse response)
    {
        LogResponse(operation, response.RequestCharge, response.Diagnostics);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Cosmos batch {Operation} failed: status={StatusCode}, activityId={ActivityId}", operation, response.StatusCode, response.ActivityId);
        }
    }

    private static void ThrowIfFailed(TransactionalBatchResponse response, string operation)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new TicketingPersistenceException(
                operation,
                response.StatusCode,
                response.RequestCharge,
                response.ActivityId);
        }
    }

    private static string CreateOrderId(string eventId, string idempotencyKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{eventId}:{idempotencyKey}"));
        return $"order-{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }

    private static void EnsureMatchingRequest(Order existingOrder, PurchaseTicketsRequest request)
    {
        if (!string.Equals(existingOrder.CustomerId, request.CustomerId, StringComparison.Ordinal) ||
            existingOrder.Quantity != request.Quantity)
        {
            throw new IdempotencyConflictException(
                "The Idempotency-Key was already used with a different customer or quantity.");
        }
    }

    private static decimal GetUnitPrice(string priceTier) => priceTier.ToLowerInvariant() switch
    {
        "economy" => 25m,
        "standard" => 50m,
        "premium" => 100m,
        "vip" => 250m,
        _ => throw new ArgumentOutOfRangeException(nameof(priceTier), "Unsupported price tier.")
    };
}
