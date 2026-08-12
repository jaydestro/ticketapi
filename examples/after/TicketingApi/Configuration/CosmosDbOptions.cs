namespace TicketingApi.Configuration;

public sealed class CosmosDbOptions
{
    public const string SectionName = "CosmosDb";

    public string AccountEndpoint { get; init; } = string.Empty;

    public string ConnectionString { get; init; } = string.Empty;

    public string ManagedIdentityClientId { get; init; } = string.Empty;

    public string DatabaseName { get; init; } = string.Empty;

    public string TicketingContainerName { get; init; } = "ticketing-write";

    public string EventsByCityContainerName { get; init; } = "events-by-city";

    public string OrdersByCustomerContainerName { get; init; } = "orders-by-customer";

    public string LeaseContainerName { get; init; } = "change-feed-leases";

    public string ChangeFeedProcessorName { get; init; } = "ticketing-read-models-v1";

    public int SlowOperationThresholdMilliseconds { get; init; } = 250;
}