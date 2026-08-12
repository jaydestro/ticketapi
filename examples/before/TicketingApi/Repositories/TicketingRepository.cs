using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using TicketingApi.Configuration;
using TicketingApi.Models;

namespace TicketingApi.Repositories;

public sealed class TicketingRepository : ITicketingRepository
{
    private readonly Container _events;
    private readonly Container _orders;

    public TicketingRepository(CosmosClient client, IOptions<CosmosDbOptions> options)
    {
        var value = options.Value;
        var database = client.GetDatabase(value.DatabaseName);
        _events = database.GetContainer(value.EventsContainerName);
        _orders = database.GetContainer(value.OrdersContainerName);
    }

    public async Task<CosmosResult<TicketEvent>> CreateEventAsync(
        TicketEvent ticketEvent,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _events.CreateItemAsync(
                ticketEvent,
                new PartitionKey(ticketEvent.Id),
                cancellationToken: cancellationToken);

            return new CosmosResult<TicketEvent>(
                response.Resource,
                response.RequestCharge,
                CosmosQueryScopes.NotApplicable);
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.TooManyRequests)
        {
            TagQueryScope(exception, CosmosQueryScopes.NotApplicable);
            throw;
        }
    }

    public async Task<CosmosResult<TicketEvent?>> GetEventAsync(
        string id,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _events.ReadItemAsync<TicketEvent>(
                id,
                new PartitionKey(id),
                cancellationToken: cancellationToken);

            return new CosmosResult<TicketEvent?>(
                response.Resource,
                response.RequestCharge,
                CosmosQueryScopes.PointRead);
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return new CosmosResult<TicketEvent?>(
                null,
                exception.RequestCharge,
                CosmosQueryScopes.PointRead);
        }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.TooManyRequests)
            {
                TagQueryScope(exception, CosmosQueryScopes.PointRead);
                throw;
            }
    }

    public Task<CosmosResult<IReadOnlyList<TicketEvent>>> GetUpcomingEventsAsync(
        CancellationToken cancellationToken)
    {
        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.eventDate >= @now ORDER BY c.eventDate")
            .WithParameter("@now", DateTime.UtcNow);

        return QueryAsync<TicketEvent>(_events, query, cancellationToken);
    }

    public Task<CosmosResult<IReadOnlyList<TicketEvent>>> GetEventsByCityAsync(
        string city,
        CancellationToken cancellationToken)
    {
        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.city = @city ORDER BY c.eventDate")
            .WithParameter("@city", city);

        return QueryAsync<TicketEvent>(_events, query, cancellationToken);
    }

    public async Task<CosmosResult<Order?>> PurchaseTicketsAsync(
        PurchaseTicketsRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var eventResult = await GetEventAsync(request.EventId, cancellationToken);
            var ticketEvent = eventResult.Value;
            if (ticketEvent is null)
            {
                return new CosmosResult<Order?>(
                    null,
                    eventResult.RequestCharge,
                    CosmosQueryScopes.NotApplicable);
            }

            if (ticketEvent.AvailableSeats < request.Quantity)
            {
                throw new InvalidOperationException("Not enough seats are available.");
            }

            ticketEvent.AvailableSeats -= request.Quantity;
            var eventResponse = await _events.ReplaceItemAsync(
                ticketEvent,
                ticketEvent.Id,
                new PartitionKey(ticketEvent.Id),
                cancellationToken: cancellationToken);

            var order = new Order
            {
                EventId = ticketEvent.Id,
                CustomerId = request.CustomerId,
                Quantity = request.Quantity,
                PriceTier = ticketEvent.PriceTier,
                TotalPrice = GetUnitPrice(ticketEvent.PriceTier) * request.Quantity
            };

            var orderResponse = await _orders.CreateItemAsync(
                order,
                new PartitionKey(order.Id),
                cancellationToken: cancellationToken);

            return new CosmosResult<Order?>(
                orderResponse.Resource,
                eventResult.RequestCharge + eventResponse.RequestCharge + orderResponse.RequestCharge,
                CosmosQueryScopes.NotApplicable);
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.TooManyRequests)
        {
            TagQueryScope(exception, CosmosQueryScopes.NotApplicable);
            throw;
        }
    }

    public Task<CosmosResult<IReadOnlyList<Order>>> GetOrdersByCustomerAsync(
        string customerId,
        CancellationToken cancellationToken)
    {
        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.customerId = @customerId ORDER BY c.orderDate DESC")
            .WithParameter("@customerId", customerId);

        return QueryAsync<Order>(_orders, query, cancellationToken);
    }

    public Task<CosmosResult<IReadOnlyList<Order>>> GetOrdersByEventAsync(
        string eventId,
        CancellationToken cancellationToken)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.eventId = @eventId")
            .WithParameter("@eventId", eventId);

        return QueryAsync<Order>(_orders, query, cancellationToken);
    }

    private static async Task<CosmosResult<IReadOnlyList<T>>> QueryAsync<T>(
        Container container,
        QueryDefinition query,
        CancellationToken cancellationToken,
        QueryRequestOptions? requestOptions = null)
    {
        var results = new List<T>();
        var requestCharge = 0d;
        using var iterator = container.GetItemQueryIterator<T>(query, requestOptions: requestOptions);

        try
        {
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync(cancellationToken);
                requestCharge += response.RequestCharge;
                results.AddRange(response);
            }
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.TooManyRequests)
        {
            TagQueryScope(
                exception,
                requestOptions?.PartitionKey is null
                    ? CosmosQueryScopes.CrossPartition
                    : CosmosQueryScopes.SinglePartition);
            throw;
        }

        return new CosmosResult<IReadOnlyList<T>>(
            results,
            requestCharge,
            requestOptions?.PartitionKey is null
                ? CosmosQueryScopes.CrossPartition
                : CosmosQueryScopes.SinglePartition);
    }

    private static void TagQueryScope(CosmosException exception, string queryScope) =>
        exception.Data[CosmosQueryScopes.ExceptionDataKey] = queryScope;

    private static decimal GetUnitPrice(string priceTier) => priceTier.ToLowerInvariant() switch
    {
        "economy" => 25m,
        "standard" => 50m,
        "premium" => 100m,
        "vip" => 250m,
        _ => 50m
    };
}