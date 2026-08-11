namespace TicketingApi.Repositories;

public sealed record CosmosResult<T>(T Value, double RequestCharge);