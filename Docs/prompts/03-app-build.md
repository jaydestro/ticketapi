# Build the Ticketing API

```text
Create a new .NET 10 ASP.NET Core Web API project for a live event ticketing platform. Keep it independent from Seeder and LoadGen. The Cosmos DB resources already exist and contain seeded data. Use `Microsoft.Azure.Cosmos`, `DefaultAzureCredential`, and the shared root `appsettings.json`; do not create project-local settings or reconfigure Azure resources.
Enable OpenAPI so LoadGen can discover the routes. Have the app run on port 5107.

The app tracks events, ticket inventory, and customer orders. Implement:

- Create an event (name, venue, city, event date, total seats, price tier)
- Get a single event by ID
- List upcoming events, sorted by event date
- List events in a city, sorted by event date
- Purchase tickets for an event by decrementing available seats and creating an order
- Get orders for a customer, most recent first
- Get orders for an event

Use model classes compatible with the documents produced by Seeder, a repository layer, and
API controllers. Keep the implementation straightforward and idiomatic C#. Build and test
the API without deleting or reseeding existing data.
```
