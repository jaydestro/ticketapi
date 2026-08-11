using TicketingApi.Models;

namespace TicketingApi.Repositories;

public interface ITicketingRepository
{
    Task<CosmosResult<TicketEvent>> CreateEventAsync(TicketEvent ticketEvent, CancellationToken cancellationToken);

    Task<CosmosResult<TicketEvent?>> GetEventAsync(string id, CancellationToken cancellationToken);

    Task<CosmosResult<IReadOnlyList<TicketEvent>>> GetUpcomingEventsAsync(CancellationToken cancellationToken);

    Task<CosmosResult<IReadOnlyList<TicketEvent>>> GetEventsByCityAsync(string city, CancellationToken cancellationToken);

    Task<CosmosResult<Order?>> PurchaseTicketsAsync(PurchaseTicketsRequest request, CancellationToken cancellationToken);

    Task<CosmosResult<IReadOnlyList<Order>>> GetOrdersByCustomerAsync(string customerId, CancellationToken cancellationToken);

    Task<CosmosResult<IReadOnlyList<Order>>> GetOrdersByEventAsync(string eventId, CancellationToken cancellationToken);
}