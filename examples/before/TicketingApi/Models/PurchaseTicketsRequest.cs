using System.ComponentModel.DataAnnotations;

namespace TicketingApi.Models;

public sealed class PurchaseTicketsRequest
{
    [Required]
    public string EventId { get; init; } = string.Empty;

    [Required]
    public string CustomerId { get; init; } = string.Empty;

    [Range(1, 20)]
    public int Quantity { get; init; }
}