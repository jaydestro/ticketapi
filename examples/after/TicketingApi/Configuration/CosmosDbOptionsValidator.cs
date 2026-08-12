using Microsoft.Extensions.Options;

namespace TicketingApi.Configuration;

public sealed class CosmosDbOptionsValidator : IValidateOptions<CosmosDbOptions>
{
    public ValidateOptionsResult Validate(string? name, CosmosDbOptions options)
    {
        var failures = new List<string>();
        var hasEndpoint = !string.IsNullOrWhiteSpace(options.AccountEndpoint);
        var hasConnectionString = !string.IsNullOrWhiteSpace(options.ConnectionString);

        if (hasEndpoint == hasConnectionString)
        {
            failures.Add("Configure exactly one of CosmosDb:AccountEndpoint or CosmosDb:ConnectionString.");
        }

        if (hasEndpoint && !Uri.TryCreate(options.AccountEndpoint, UriKind.Absolute, out _))
        {
            failures.Add("CosmosDb:AccountEndpoint must be an absolute URI.");
        }

        ValidateRequired(options.DatabaseName, "DatabaseName", failures);
        ValidateRequired(options.TicketingContainerName, "TicketingContainerName", failures);
        ValidateRequired(options.EventsByCityContainerName, "EventsByCityContainerName", failures);
        ValidateRequired(options.OrdersByCustomerContainerName, "OrdersByCustomerContainerName", failures);
        ValidateRequired(options.LeaseContainerName, "LeaseContainerName", failures);
        ValidateRequired(options.ChangeFeedProcessorName, "ChangeFeedProcessorName", failures);

        var containerNames = new[]
        {
            options.TicketingContainerName,
            options.EventsByCityContainerName,
            options.OrdersByCustomerContainerName,
            options.LeaseContainerName
        };
        if (containerNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != containerNames.Length)
        {
            failures.Add("Cosmos DB container names must be distinct.");
        }

        if (options.SlowOperationThresholdMilliseconds is < 1 or > 60_000)
        {
            failures.Add("CosmosDb:SlowOperationThresholdMilliseconds must be between 1 and 60000.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateRequired(string value, string propertyName, ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"CosmosDb:{propertyName} is required.");
        }
    }
}