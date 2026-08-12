using System.ComponentModel.DataAnnotations;

namespace TicketingApi.Models;

public sealed class CreateEventRequest
{
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string Name { get; init; } = string.Empty;

    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string Venue { get; init; } = string.Empty;

    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string City { get; init; } = string.Empty;

    public DateTimeOffset EventDate { get; init; }

    [Range(1, int.MaxValue)]
    public int TotalSeats { get; init; }

    [Required]
    [RegularExpression("(?i)^(economy|standard|premium|vip)$")]
    public string PriceTier { get; init; } = string.Empty;
}