using System.Text.Json.Serialization;

namespace Seeder.Models;

public class Order
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string EventId { get; set; } = string.Empty;

    public string CustomerId { get; set; } = string.Empty;

    public int Quantity { get; set; }

    public string PriceTier { get; set; } = string.Empty;

    public decimal TotalPrice { get; set; }

    public string Status { get; set; } = "confirmed";

    public DateTime OrderDate { get; set; } = DateTime.UtcNow;
}
