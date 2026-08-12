using System.ComponentModel.DataAnnotations;
using TicketingApi.Models;

namespace TicketingApi.Tests;

public sealed class RequestValidationTests
{
    [Fact]
    public void AcceptsSupportedPriceTierCaseInsensitively()
    {
        var request = ValidEvent(priceTier: "VIP");

        Assert.Empty(Validate(request));
    }

    [Fact]
    public void RejectsUnsupportedPriceTierAndOversizedName()
    {
        var request = ValidEvent(priceTier: "free", name: new string('x', 201));

        Assert.Equal(2, Validate(request).Count);
    }

    [Theory]
    [InlineData("bad/id")]
    [InlineData(" leading-space")]
    [InlineData("")]
    public void RejectsUnsafeEventIdentifiers(string eventId)
    {
        var request = new PurchaseTicketsRequest
        {
            EventId = eventId,
            CustomerId = "customer-1",
            Quantity = 1
        };

        Assert.NotEmpty(Validate(request));
    }

    private static List<ValidationResult> Validate(object value)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(value, new ValidationContext(value), results, validateAllProperties: true);
        return results;
    }

    private static CreateEventRequest ValidEvent(
        string priceTier = "standard",
        string name = "Show") => new()
    {
        Name = name,
        Venue = "Venue",
        City = "Seattle",
        EventDate = DateTimeOffset.UtcNow.AddDays(1),
        TotalSeats = 100,
        PriceTier = priceTier
    };
}