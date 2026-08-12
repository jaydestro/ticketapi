using System.Net;

namespace TicketingApi.Repositories;

public sealed class TicketingPersistenceException(
    string operation,
    HttpStatusCode statusCode,
    double requestCharge,
    string activityId)
    : Exception($"Cosmos operation '{operation}' failed with HTTP {(int)statusCode} ({statusCode}).")
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public double RequestCharge { get; } = requestCharge;

    public string ActivityId { get; } = activityId;
}