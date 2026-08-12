# Ticketing API (After)

This implementation uses an event-partitioned transactional write model and change-feed materialized views for its dominant reads.

## Cosmos DB schema

The database must already exist. Startup creates missing containers and fails if an existing container has an incompatible partition key or indexing policy.

| Container | Partition key | Purpose |
| --- | --- | --- |
| `ticketing-write` | `/eventId` | Events, inventory, and orders; enables atomic inventory and order writes |
| `events-by-city` | `/cityKey` | City and upcoming-event reads |
| `orders-by-customer` | `/customerId` | Customer-order reads |
| `change-feed-leases` | `/id` | Change-feed processor leases |

Partition keys are immutable. The original `/id` containers are not compatible with this schema. Create the replacement containers and migrate or reseed the source data before cutover. The startup backfill builds current projections, then change feed maintains them with deterministic, replay-safe upserts.

City and customer endpoints are eventually consistent with successful writes. Event inventory and event-order reads use the transactional source container. Upcoming events is the only intentional cross-partition query.

## Configuration

Copy the shape in `appsettings.json.example` into environment-specific configuration. Configure exactly one Cosmos authentication mode:

- `CosmosDb:AccountEndpoint` uses `DefaultAzureCredential`; set `ManagedIdentityClientId` for a user-assigned identity.
- `CosmosDb:ConnectionString` supports the local emulator or key authentication.

Outside the `Development` environment, both `Authentication:Authority` and `Authentication:Audience` are required. JWT bearer authentication protects API and OpenAPI routes. `/health/live` and `/health/ready` remain anonymous for probes.

## HTTP contracts

List routes preserve array response bodies. Set `pageSize` from 1 through 100 and pass the URL-safe token returned in `x-continuation-token` to request the next page.

Purchases require an `Idempotency-Key` header. Repeating the same event, customer, quantity, and key returns the original order without decrementing inventory again. Reusing a key with a different customer or quantity returns HTTP 409.

Successful and failed database responses expose request charge and query-scope headers when available. Detailed Cosmos diagnostics are logged server-side and are not returned to clients.

## Verification

```powershell
dotnet build TicketingApi.csproj -c Release
dotnet test Tests/TicketingApi.Tests.csproj -c Release
```

Before production cutover, run concurrency and replay checks against the target Cosmos account or emulator: simultaneous purchases must not oversell, repeated idempotency keys must produce one order, and city/customer projections must converge after change-feed processing.
