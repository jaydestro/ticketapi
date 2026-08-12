using TicketingApi.Configuration;
using TicketingApi.Cosmos;

namespace TicketingApi.Tests;

public sealed class CosmosContainerDefinitionsTests
{
    [Fact]
    public void DefinesExpectedPartitionKeysAndIndexes()
    {
        var definitions = CosmosContainerDefinitions.Create(new CosmosDbOptions());

        Assert.Collection(
            definitions,
            write =>
            {
                Assert.Equal("/eventId", write.PartitionKeyPath);
                Assert.Equal(["/eventId/?", "/orderDate/?", "/type/?"], write.IndexingPolicy.IncludedPaths.Select(path => path.Path).Order());
                Assert.Equal(["/*", "/\"_etag\"/?"], write.IndexingPolicy.ExcludedPaths.Select(path => path.Path));
            },
            city =>
            {
                Assert.Equal("/cityKey", city.PartitionKeyPath);
                Assert.Equal(["/cityKey/?", "/eventDate/?"], city.IndexingPolicy.IncludedPaths.Select(path => path.Path).Order());
            },
            customer =>
            {
                Assert.Equal("/customerId", customer.PartitionKeyPath);
                Assert.Equal(["/customerId/?", "/orderDate/?"], customer.IndexingPolicy.IncludedPaths.Select(path => path.Path).Order());
            },
            leases => Assert.Equal("/id", leases.PartitionKeyPath));
    }
}