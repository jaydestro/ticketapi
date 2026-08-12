using System.ComponentModel.DataAnnotations;

namespace TicketingApi.Models;

public sealed class PurchaseTicketsRequest
{
    [Required]
    [StringLength(128, MinimumLength = 1)]
    [RegularExpression(@"^[A-Za-z0-9][A-Za-z0-9._:-]*$")]
    public string EventId { get; init; } = string.Empty;

    [Required]
    [StringLength(128, MinimumLength = 1)]
    [RegularExpression(@"^[A-Za-z0-9][A-Za-z0-9._:-]*$")]
    public string CustomerId { get; init; } = string.Empty;

    [Range(1, 20)]
    public int Quantity { get; init; }
}