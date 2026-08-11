using System.Text.Json.Serialization;

namespace TicketingApi.Models;

public sealed class TicketEvent
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string Name { get; set; } = string.Empty;

    public string Venue { get; set; } = string.Empty;

    public string City { get; set; } = string.Empty;

    public DateTime EventDate { get; set; }

    public int TotalSeats { get; set; }

    public int AvailableSeats { get; set; }

    public string PriceTier { get; set; } = string.Empty;
}