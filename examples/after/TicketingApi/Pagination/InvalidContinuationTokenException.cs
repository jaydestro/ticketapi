namespace TicketingApi.Pagination;

public sealed class InvalidContinuationTokenException(string message, Exception innerException)
    : Exception(message, innerException);