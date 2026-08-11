namespace LoadGen.Tests;

public sealed class RouteMatchingTests
{
    public static TheoryData<string, string[], string?> Cases => new()
    {
        { "get", new[] { "api", "events", "{id}" }, nameof(RequestKind.EventDetail) },
        { "GET", new[] { "API", "EVENTS", "UPCOMING" }, nameof(RequestKind.UpcomingEvents) },
        { "GET", new[] { "api", "events", "city", "{city}" }, nameof(RequestKind.EventsByCity) },
        { "POST", new[] { "api", "events" }, nameof(RequestKind.CreateEvent) },
        { "POST", new[] { "api", "orders" }, nameof(RequestKind.PurchaseTicket) },
        { "GET", new[] { "api", "orders", "customer", "{customerId}" }, nameof(RequestKind.OrdersByCustomer) },
        { "GET", new[] { "api", "orders", "event", "{eventId}" }, nameof(RequestKind.OrdersByEvent) },
        { "DELETE", new[] { "api", "events", "{id}" }, null },
        { "GET", new[] { "api", "unknown" }, null }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void MatchKind_maps_only_supported_method_and_path_shapes(
        string method,
        string[] segments,
        string? expected)
    {
        Assert.Equal(expected, LoadGenRoutes.MatchKind(method, segments)?.ToString());
    }
}