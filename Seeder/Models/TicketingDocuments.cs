using System.Text.Json.Serialization;

namespace Seeder.Models;

public sealed class TicketEventDocument
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;
    public string EventId { get; init; } = string.Empty;
    public string Type { get; init; } = "event";
    public int SchemaVersion { get; init; } = 1;
    public string Name { get; init; } = string.Empty;
    public string Venue { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public DateTime EventDate { get; init; }
    public int TotalSeats { get; init; }
    public int AvailableSeats { get; init; }
    public string PriceTier { get; init; } = string.Empty;

    public static TicketEventDocument FromModel(TicketEvent value) => new()
    {
        Id = value.Id,
        EventId = value.Id,
        Name = value.Name,
        Venue = value.Venue,
        City = value.City,
        EventDate = value.EventDate,
        TotalSeats = value.TotalSeats,
        AvailableSeats = value.AvailableSeats,
        PriceTier = value.PriceTier
    };
}

public sealed class OrderDocument
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;
    public string EventId { get; init; } = string.Empty;
    public string Type { get; init; } = "order";
    public int SchemaVersion { get; init; } = 1;
    public string CustomerId { get; init; } = string.Empty;
    public int Quantity { get; init; }
    public string PriceTier { get; init; } = string.Empty;
    public decimal TotalPrice { get; init; }
    public string Status { get; init; } = "confirmed";
    public DateTime OrderDate { get; init; }

    public static OrderDocument FromModel(Order value) => new()
    {
        Id = value.Id,
        EventId = value.EventId,
        CustomerId = value.CustomerId,
        Quantity = value.Quantity,
        PriceTier = value.PriceTier,
        TotalPrice = value.TotalPrice,
        Status = value.Status,
        OrderDate = value.OrderDate
    };
}

public sealed class EventByCityDocument
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;
    public string CityKey { get; init; } = string.Empty;
    public string EventId { get; init; } = string.Empty;
    public string Type { get; init; } = "event";
    public int SchemaVersion { get; init; } = 1;
    public string Name { get; init; } = string.Empty;
    public string Venue { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public DateTime EventDate { get; init; }
    public int TotalSeats { get; init; }
    public int AvailableSeats { get; init; }
    public string PriceTier { get; init; } = string.Empty;

    public static EventByCityDocument FromModel(TicketEvent value) => new()
    {
        Id = $"event:{value.Id}",
        CityKey = value.City.Trim().ToUpperInvariant(),
        EventId = value.Id,
        Name = value.Name,
        Venue = value.Venue,
        City = value.City,
        EventDate = value.EventDate,
        TotalSeats = value.TotalSeats,
        AvailableSeats = value.AvailableSeats,
        PriceTier = value.PriceTier
    };
}

public sealed class OrderByCustomerDocument
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;
    public string CustomerId { get; init; } = string.Empty;
    public string EventId { get; init; } = string.Empty;
    public int SchemaVersion { get; init; } = 1;
    public int Quantity { get; init; }
    public string PriceTier { get; init; } = string.Empty;
    public decimal TotalPrice { get; init; }
    public string Status { get; init; } = string.Empty;
    public DateTime OrderDate { get; init; }

    public static OrderByCustomerDocument FromModel(Order value) => new()
    {
        Id = $"order:{value.Id}",
        CustomerId = value.CustomerId,
        EventId = value.EventId,
        Quantity = value.Quantity,
        PriceTier = value.PriceTier,
        TotalPrice = value.TotalPrice,
        Status = value.Status,
        OrderDate = value.OrderDate
    };
}

public sealed class ReadModelBackfillMarker
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "ticketing-read-models-v1-backfill";
    public string CityKey { get; init; } = "__SYSTEM__";
    public string Type { get; init; } = "metadata";
    public DateTime CompletedAt { get; init; } = DateTime.UtcNow;
}