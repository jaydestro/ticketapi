```
Build me a REST API for a live event ticketing platform using Azure Cosmos DB and .NET. I want to get something working quickly. 

Use ASP.NET Core Web API and the Microsoft.Azure.Cosmos SDK. The Cosmos DB account, database, and the events and orders containers already exist, and the project is already configured to authenticate with Entra ID — use the existing credential setup. Use OpenAPI to make endpoints discoverable.

The app tracks events, ticket inventory, and customer orders. I need these 
endpoints:

- Create an event (name, venue, city, event date, total seats, price tier)
- Get a single event by id
- List all upcoming events, sorted by event date
- List events in a given city, sorted by event date
- Purchase a ticket for an event (decrement available seats, create an order)
- Get all orders for a customer, most recent first
- Get all orders for an event

Include the model classes, a repository layer, and the API controllers. Keep it 
straightforward and idiomatic C#.
```