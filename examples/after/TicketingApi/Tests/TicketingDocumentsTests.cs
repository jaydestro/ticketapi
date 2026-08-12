using TicketingApi.Cosmos;

namespace TicketingApi.Tests;

public sealed class TicketingDocumentsTests
{
    [Fact]
    public void EventProjectionHasDeterministicIdentityAndNormalizedPartitionKey()
    {
        var source = new TicketEventDocument
        {
            Id = "event-1",
            EventId = "event-1",
            Name = "Show",
            Venue = "Venue",
            City = "  Seattle ",
            EventDate = DateTime.UtcNow.AddDays(1),
            TotalSeats = 100,
            AvailableSeats = 90,
            PriceTier = "standard"
        };

        var first = EventByCityDocument.FromSource(source);
        var replay = EventByCityDocument.FromSource(source);

        Assert.Equal("event:event-1", first.Id);
        Assert.Equal("SEATTLE", first.CityKey);
        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(90, first.ToModel().AvailableSeats);
    }

    [Fact]
    public void OrderProjectionRoundTripsSourceOrderIdentity()
    {
        var source = new OrderDocument
        {
            Id = "order-1",
            EventId = "event-1",
            CustomerId = "customer-1",
            Quantity = 2,
            PriceTier = "vip",
            TotalPrice = 500,
            Status = "confirmed",
            OrderDate = DateTime.UtcNow
        };

        var projection = OrderByCustomerDocument.FromSource(source);
        var model = projection.ToModel();

        Assert.Equal("order:order-1", projection.Id);
        Assert.Equal("order-1", model.Id);
        Assert.Equal("customer-1", projection.CustomerId);
    }
}