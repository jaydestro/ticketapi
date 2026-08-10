namespace Seeder.Models;

public class CosmosDbOptions
{
    // Local emulator dev path: key-based connection string (e.g. https://localhost:8081/).
    // Leave empty when using a live Azure Cosmos DB account with Entra ID auth.
    public string ConnectionString { get; set; } = string.Empty;

    // Live Azure Cosmos DB account endpoint (e.g. https://my-account.documents.azure.com:443/).
    // When set, the client authenticates with Microsoft Entra ID via DefaultAzureCredential
    // instead of an account key.
    public string AccountEndpoint { get; set; } = string.Empty;

    public string DatabaseName { get; set; } = "TicketingDb";

    public string EventsContainerName { get; set; } = "Events";

    public string OrdersContainerName { get; set; } = "Orders";
}
