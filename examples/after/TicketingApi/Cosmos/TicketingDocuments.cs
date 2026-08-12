using System.Text.Json.Serialization;
using TicketingApi.Models;

namespace TicketingApi.Cosmos;

internal static class TicketingDocumentTypes
{
    public const string Event = "event";
    public const string Order = "order";
}

internal sealed class TicketEventDocument
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;
    public string EventId { get; init; } = string.Empty;
    public string Type { get; init; } = TicketingDocumentTypes.Event;
    public int SchemaVersion { get; init; } = 1;
    public string Name { get; init; } = string.Empty;
    public string Venue { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public DateTime EventDate { get; init; }
    public int TotalSeats { get; init; }
    public int AvailableSeats { get; set; }
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

    public TicketEvent ToModel() => new()
    {
        Id = Id,
        Name = Name,
        Venue = Venue,
        City = City,
        EventDate = EventDate,
        TotalSeats = TotalSeats,
        AvailableSeats = AvailableSeats,
        PriceTier = PriceTier
    };
}

internal sealed class OrderDocument
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;
    public string EventId { get; init; } = string.Empty;
    public string Type { get; init; } = TicketingDocumentTypes.Order;
    public int SchemaVersion { get; init; } = 1;
    public string CustomerId { get; init; } = string.Empty;
    public int Quantity { get; init; }
    public string PriceTier { get; init; } = string.Empty;
    public decimal TotalPrice { get; init; }
    public string Status { get; init; } = "confirmed";
    public DateTime OrderDate { get; init; }

    public Order ToModel() => new()
    {
        Id = Id,
        EventId = EventId,
        CustomerId = CustomerId,
        Quantity = Quantity,
        PriceTier = PriceTier,
        TotalPrice = TotalPrice,
        Status = Status,
        OrderDate = OrderDate
    };
}

internal sealed class EventByCityDocument
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;
    public string CityKey { get; init; } = string.Empty;
    public string EventId { get; init; } = string.Empty;
    public string Type { get; init; } = TicketingDocumentTypes.Event;
    public int SchemaVersion { get; init; } = 1;
    public string Name { get; init; } = string.Empty;
    public string Venue { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public DateTime EventDate { get; init; }
    public int TotalSeats { get; init; }
    public int AvailableSeats { get; init; }
    public string PriceTier { get; init; } = string.Empty;

    public static EventByCityDocument FromSource(TicketEventDocument value) => new()
    {
        Id = $"event:{value.Id}",
        CityKey = NormalizeCity(value.City),
        EventId = value.Id,
        Name = value.Name,
        Venue = value.Venue,
        City = value.City,
        EventDate = value.EventDate,
        TotalSeats = value.TotalSeats,
        AvailableSeats = value.AvailableSeats,
        PriceTier = value.PriceTier
    };

    public TicketEvent ToModel() => new()
    {
        Id = EventId,
        Name = Name,
        Venue = Venue,
        City = City,
        EventDate = EventDate,
        TotalSeats = TotalSeats,
        AvailableSeats = AvailableSeats,
        PriceTier = PriceTier
    };

    public static string NormalizeCity(string city) => city.Trim().ToUpperInvariant();
}

internal sealed class OrderByCustomerDocument
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

    public static OrderByCustomerDocument FromSource(OrderDocument value) => new()
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

    public Order ToModel() => new()
    {
        Id = Id["order:".Length..],
        EventId = EventId,
        CustomerId = CustomerId,
        Quantity = Quantity,
        PriceTier = PriceTier,
        TotalPrice = TotalPrice,
        Status = Status,
        OrderDate = OrderDate
    };
}
