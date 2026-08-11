# Create the Cosmos DB Ticketing API

```text
This workspace currently contains two decoupled .NET 10 console tools:

- Seeder creates deterministic TicketEvent and Order documents in Azure Cosmos DB.
- LoadGen sends traffic to a ticketing HTTP API and records the Cosmos DB RU charge returned
  in each response.

There is no web app yet. Scaffold a brand-new ASP.NET Core Web API project in this workspace
and implement the API contract that these tools already expect. The API must use Azure Cosmos
DB for NoSQL with Microsoft Entra ID (passwordless) authentication against a live Azure
account.

Inspect the existing branch before writing code. Reuse its behavioral and configuration
contracts, but do not add project references from the API to Seeder or LoadGen; keep all three
projects independently buildable.

Requirements:

1. Create a new ASP.NET Core Web API project (`dotnet new webapi`) in its own project/folder,
   targeting `net10.0`. Use the same compatible versions of `Microsoft.Azure.Cosmos`,
   `Azure.Identity`, and `Newtonsoft.Json` already used by Seeder. Keep OpenAPI enabled and
   expose its document at `/openapi/v1.json`, which LoadGen probes for route discovery.

2. Use the existing repository-root `appsettings.json`. Do not create a project-local
   `appsettings.json`. `Directory.Build.targets` already links the root file into every
   project's build and publish output.

   Update only the blank values in the root `CosmosDb` section after I provide them:
   - `AccountEndpoint`: live account URI, such as
     `https://<account>.documents.azure.com:443/`
   - `DatabaseName`
   - `EventsContainerName`
   - `OrdersContainerName`

   Preserve the existing comments and `ConnectionString` key because Seeder supports a local
   emulator/key-based path. Leave `ConnectionString` empty. The new API itself must use
   `AccountEndpoint` plus Entra ID only and must not fall back to `ConnectionString`.

3. Add a `CosmosDbOptions` class bound to the `CosmosDb` section with `AccountEndpoint`,
   `DatabaseName`, `EventsContainerName`, and `OrdersContainerName`. Validate all four values
   at startup and report a clear error naming any missing setting.

4. Register one `CosmosClient` singleton for the application's lifetime:
   - Use `new DefaultAzureCredential()` and the `AccountEndpoint` constructor overload.
   - Set `UseSystemTextJsonSerializerWithOptions` to options created with
     `JsonSerializerDefaults.Web`, so `[JsonPropertyName("id")]` is honored.
   - Use async SDK calls and dispose the client through application lifetime management.
   - Do not hardcode credentials, account endpoints, database names, or container names.

5. Add startup initialization that obtains the configured database and containers and calls
   `CreateDatabaseIfNotExistsAsync` and `CreateContainerIfNotExistsAsync` with
   `PartitionKeyPath = "/id"`. Do not pass explicit manual or autoscale throughput; the live
   account may be serverless.

   A data-plane contributor cannot create a brand-new database. The database and containers
   therefore must be pre-created through the Azure control plane before first startup when
   the app identity only has the built-in data contributor role. The startup calls then act as
   existence checks. Do not silently swallow initialization failures; log enough context to
   make configuration or RBAC failures actionable without exposing credentials.

6. Define API-local models compatible with the existing Seeder documents. Preserve the exact
   JSON shape and `[JsonPropertyName("id")]` mapping found in:
   - `Seeder/Models/TicketEvent.cs`
   - `Seeder/Models/Order.cs`
   - `Seeder/Models/PriceTiers.cs`

   Do not reference or move Seeder's model classes. For create requests, use dedicated request
   DTOs rather than binding persisted entities directly. Validate IDs, quantities, dates,
   required strings, seat counts, and supported price tiers.

7. Implement all routes currently used by LoadGen, preserving these methods and paths:
   - `GET /api/events/{id}`
   - `GET /api/events/upcoming`
   - `GET /api/events/city/{city}`
   - `POST /api/events`
   - `POST /api/orders`
   - `GET /api/orders/customer/{customerId}`
   - `GET /api/orders/event/{eventId}`

   Match the request bodies emitted by `LoadGen/Program.cs`. Event creation receives `name`,
   `venue`, `city`, `eventDate`, `totalSeats`, and `priceTier`; assign a new ID and initialize
   `AvailableSeats`. Order creation receives `eventId`, `customerId`, and `quantity`; read the
   event, reject invalid or unavailable purchases, calculate the price using the existing tier
   catalog, and create a confirmed order.

8. Use point reads when both `id` and partition key are known. Use parameterized Cosmos DB
   queries for city, upcoming-event, customer-order, and event-order lookups. Bound list
   responses and implement continuation-token pagination so these endpoints cannot read an
   entire container into memory. Keep the existing `/id` partition-key contract even though
   those secondary lookups are cross-partition queries.

9. Add the total Cosmos DB request charge consumed by each operation to the HTTP response
   header `x-ms-request-charge`. Aggregate charges when an API operation performs multiple
   Cosmos DB calls or reads multiple query pages. LoadGen depends on this exact header name for
   its live and final RU calculations.

10. Return appropriate HTTP status codes and problem details: 400 for invalid input, 404 for
    missing events/orders, 409 for conflicts such as insufficient seats, and 503 for Cosmos DB
    availability failures. Handle `CosmosException` deliberately, preserve cancellation
    tokens, and add structured logging without leaking secrets.

11. Add focused tests for configuration validation, request validation, route behavior, and
    RU-header propagation. Do not require a live Azure account for the normal test run. Build
    and test every project in the workspace, then verify that the generated OpenAPI document
    contains all seven LoadGen routes.

12. Update the root README with prerequisites and exact commands to configure, build, seed,
    run the API, and run LoadGen. Explain that configuration is owned by the single root
    `appsettings.json` and shared by `Directory.Build.targets`.

IMPORTANT - RBAC:

Entra ID authentication to Cosmos DB requires a data-plane role assignment in addition to any
Azure control-plane RBAC role. The runtime identity needs the built-in
`Cosmos DB Built-in Data Contributor` role scoped to this account:

az cosmosdb sql role assignment create --account-name <account> --resource-group <rg>
  --role-definition-id 00000000-0000-0000-0000-000000000002
  --principal-id <principal-object-id> --scope /

This role can read and write items but cannot create the database. Pre-create the database and
containers through the control plane using an identity with suitable Azure RBAC permissions.
Present the required commands as manual follow-up steps, but do not execute Azure resource
creation or role assignment unless I explicitly approve it and provide the account name,
resource group, and principal ID.

Before implementation, ask me for: the new project name, Cosmos DB account name and resource
group, database and container names, and whether the runtime identity is my current `az login`
user or a managed identity. After receiving those answers, implement and validate the work end
to end rather than stopping at a plan.
```
