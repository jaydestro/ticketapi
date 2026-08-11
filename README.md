# Ticketing API Demo

This repository builds a live event ticketing API on Azure Cosmos DB through a sequence of
prompts. The finished system demonstrates keyless Azure authentication, repeatable
infrastructure, deterministic test data, a .NET REST API, workload generation, and iterative
Cosmos DB design reviews.

## Build sequence

Run the numbered prompts in order.

### 01 - Create Cosmos DB and configure Entra ID

[01-create-cosmos-entra.md](Docs/prompts/01-create-cosmos-entra.md) creates the Azure foundation:

- Subscription-scope Bicep infrastructure
- A resource group and user-assigned managed identity
- A serverless Azure Cosmos DB for NoSQL account with key authentication disabled
- A database and `/id`-partitioned events and orders containers
- Cosmos DB data-plane RBAC for local development and the hosted application
- A generated root `appsettings.json` based on `appsettings.json.example`

### 02 - Seed ticketing data

[02-seeder.md](Docs/prompts/02-seeder.md) builds and runs the existing Seeder to create a
deterministic workload:

- 5,000 events
- 250,000 customer orders
- A deliberately skewed hot event for scale and query testing
- Post-run validation of counts, skew, RU consumption, and elapsed time

Deterministic IDs and upserts make the seed operation repeatable without increasing document
counts.

### 03 - Build the API

[03-app-build.md](Docs/prompts/03-app-build.md) creates the .NET 10 ASP.NET Core Web API over
the provisioned and seeded Cosmos DB data. It adds:

- Event creation and lookup endpoints
- Upcoming-event and city queries
- Ticket purchasing and inventory updates
- Customer and event order queries
- API-local models, repositories, controllers, and OpenAPI
- Keyless access through `DefaultAzureCredential`

### 04 - Review through four lenses

[04-four-lens-prompts.md](Docs/prompts/04-four-lens-prompts.md) contains four separate review
passes. Run them one at a time to examine and improve:

1. Data-model scalability
2. Query cost
3. Indexing-policy cost
4. Production readiness

## Supporting projects

### Seeder

`Seeder` generates reproducible event and order documents with a controlled traffic skew. It
also reports total request units and elapsed time for the bulk operation.

### LoadGen

`LoadGen` drives concurrent traffic against the completed API, discovers routes through
OpenAPI, simulates periodic purchase bursts, and reports Cosmos DB request-unit consumption by
endpoint.

## Shared configuration

`appsettings.json.example` defines the required Cosmos DB settings. Prompt 01 creates the
ignored root `appsettings.json`, and `Directory.Build.targets` shares it with every project.
No credentials or account keys belong in the repository.
