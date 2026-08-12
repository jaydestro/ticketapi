using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using TicketingApi.Configuration;

namespace TicketingApi.Cosmos;

public sealed class CosmosDbInitializer(
    CosmosClient client,
    IOptions<CosmosDbOptions> options,
    CosmosReadinessState readiness,
    ILogger<CosmosDbInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var database = client.GetDatabase(options.Value.DatabaseName);
            await database.ReadAsync(cancellationToken: cancellationToken);

            foreach (var expected in CosmosContainerDefinitions.Create(options.Value))
            {
                var response = await database.CreateContainerIfNotExistsAsync(
                    expected,
                    cancellationToken: cancellationToken);
                ValidatePartitionKey(response.Resource, expected);

                if (!IndexingPolicyMatches(response.Resource, expected))
                {
                    logger.LogInformation(
                        "Updating Cosmos DB indexing policy for container {Container}",
                        expected.Id);
                    var replacement = await database
                        .GetContainer(expected.Id)
                        .ReplaceContainerAsync(expected, cancellationToken: cancellationToken);
                    ValidatePartitionKey(replacement.Resource, expected);
                    if (!IndexingPolicyMatches(replacement.Resource, expected))
                    {
                        throw new InvalidOperationException(
                            $"Container '{expected.Id}' indexing policy could not be updated to match the production query contract.");
                    }
                }
            }

            readiness.SchemaReady = true;
            logger.LogInformation("Cosmos DB schema validation completed for {Database}", options.Value.DatabaseName);
        }
        catch (Exception exception)
        {
            readiness.Failure = exception.Message;
            logger.LogCritical(exception, "Cosmos DB schema initialization failed");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static void ValidatePartitionKey(ContainerProperties actual, ContainerProperties expected)
    {
        if (!string.Equals(actual.PartitionKeyPath, expected.PartitionKeyPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Container '{actual.Id}' uses partition key '{actual.PartitionKeyPath}', expected '{expected.PartitionKeyPath}'. " +
                "Partition keys are immutable; create a replacement container and migrate/reseed data.");
        }
    }

    private static bool IndexingPolicyMatches(ContainerProperties actual, ContainerProperties expected)
    {
        if (expected.Id.Contains("lease", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var actualIncluded = actual.IndexingPolicy.IncludedPaths.Select(path => path.Path).Order().ToArray();
        var expectedIncluded = expected.IndexingPolicy.IncludedPaths.Select(path => path.Path).Order().ToArray();
        var actualExcluded = actual.IndexingPolicy.ExcludedPaths.Select(path => path.Path).Order().ToArray();
        var expectedExcluded = expected.IndexingPolicy.ExcludedPaths.Select(path => path.Path).Order().ToArray();

        return actual.IndexingPolicy.IndexingMode == expected.IndexingPolicy.IndexingMode &&
            actual.IndexingPolicy.Automatic == expected.IndexingPolicy.Automatic &&
            actualIncluded.SequenceEqual(expectedIncluded, StringComparer.Ordinal) &&
            actualExcluded.SequenceEqual(expectedExcluded, StringComparer.Ordinal);
    }
}