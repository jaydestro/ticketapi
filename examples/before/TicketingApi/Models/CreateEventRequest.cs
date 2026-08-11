using System.ComponentModel.DataAnnotations;

namespace TicketingApi.Models;

public sealed class CreateEventRequest
{
    [Required]
    public string Name { get; init; } = string.Empty;

    [Required]
    public string Venue { get; init; } = string.Empty;

    [Required]
    public string City { get; init; } = string.Empty;

    public DateTime EventDate { get; init; }

    [Range(1, int.MaxValue)]
    public int TotalSeats { get; init; }

    [Required]
    public string PriceTier { get; init; } = string.Empty;
}