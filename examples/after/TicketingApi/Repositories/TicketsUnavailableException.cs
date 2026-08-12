namespace TicketingApi.Repositories;

public sealed class TicketsUnavailableException(string message) : Exception(message);
