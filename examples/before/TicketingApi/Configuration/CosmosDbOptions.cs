using System.ComponentModel.DataAnnotations;

namespace TicketingApi.Configuration;

public sealed class CosmosDbOptions
{
    public const string SectionName = "CosmosDb";

    [Required]
    public string AccountEndpoint { get; init; } = string.Empty;

    [Required]
    public string DatabaseName { get; init; } = string.Empty;

    [Required]
    public string EventsContainerName { get; init; } = string.Empty;

    [Required]
    public string OrdersContainerName { get; init; } = string.Empty;
}