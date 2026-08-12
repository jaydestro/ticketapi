using Microsoft.Azure.Cosmos;
using TicketingApi.Configuration;

namespace TicketingApi.Cosmos;

public static class CosmosContainerDefinitions
{
    public static IReadOnlyList<ContainerProperties> Create(CosmosDbOptions options) =>
    [
        CreateProperties(options.TicketingContainerName, "/eventId", ["/eventId/?", "/type/?", "/orderDate/?"]),
        CreateProperties(options.EventsByCityContainerName, "/cityKey", ["/cityKey/?", "/eventDate/?"]),
        CreateProperties(options.OrdersByCustomerContainerName, "/customerId", ["/customerId/?", "/orderDate/?"]),
        new ContainerProperties(options.LeaseContainerName, "/id")
    ];

    private static ContainerProperties CreateProperties(
        string name,
        string partitionKeyPath,
        IReadOnlyCollection<string> includedPaths)
    {
        var properties = new ContainerProperties(name, partitionKeyPath)
        {
            IndexingPolicy = new IndexingPolicy
            {
                Automatic = true,
                IndexingMode = IndexingMode.Consistent
            }
        };

        properties.IndexingPolicy.IncludedPaths.Clear();
        foreach (var path in includedPaths)
        {
            properties.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = path });
        }
        properties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/*" });
        properties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/\"_etag\"/?" });
        return properties;
    }
}