using TicketingApi.Models;
using TicketingApi.Pagination;

namespace TicketingApi.Repositories;

public interface ITicketingRepository
{
    Task<CosmosResult<TicketEvent>> CreateEventAsync(TicketEvent ticketEvent, CancellationToken cancellationToken);

    Task<CosmosResult<TicketEvent?>> GetEventAsync(string id, CancellationToken cancellationToken);

    Task<CosmosPage<TicketEvent>> GetUpcomingEventsAsync(int pageSize, string? continuationToken, CancellationToken cancellationToken);

    Task<CosmosPage<TicketEvent>> GetEventsByCityAsync(string city, int pageSize, string? continuationToken, CancellationToken cancellationToken);

    Task<CosmosResult<Order?>> PurchaseTicketsAsync(PurchaseTicketsRequest request, string idempotencyKey, CancellationToken cancellationToken);

    Task<CosmosPage<Order>> GetOrdersByCustomerAsync(string customerId, int pageSize, string? continuationToken, CancellationToken cancellationToken);

    Task<CosmosPage<Order>> GetOrdersByEventAsync(string eventId, int pageSize, string? continuationToken, CancellationToken cancellationToken);
}