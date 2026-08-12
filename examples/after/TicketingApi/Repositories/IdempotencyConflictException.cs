namespace TicketingApi.Repositories;

public sealed class IdempotencyConflictException(string message) : Exception(message);