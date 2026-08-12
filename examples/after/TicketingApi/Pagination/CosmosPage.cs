namespace TicketingApi.Pagination;

public sealed record CosmosPage<T>(
    IReadOnlyList<T> Items,
    string? ContinuationToken,
    double RequestCharge,
    string QueryScope);
