using Microsoft.AspNetCore.Mvc;
using TicketingApi.Models;
using TicketingApi.Repositories;

namespace TicketingApi.Controllers;

[ApiController]
[Route("api/events")]
public sealed class EventsController(ITicketingRepository repository) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<TicketEvent>> Create(
        CreateEventRequest request,
        CancellationToken cancellationToken)
    {
        var ticketEvent = new TicketEvent
        {
            Name = request.Name,
            Venue = request.Venue,
            City = request.City,
            EventDate = request.EventDate,
            TotalSeats = request.TotalSeats,
            AvailableSeats = request.TotalSeats,
            PriceTier = request.PriceTier
        };

        var result = await repository.CreateEventAsync(ticketEvent, cancellationToken);
        AddCosmosMetadata(result.RequestCharge, result.QueryScope);
        return CreatedAtAction(nameof(GetById), new { id = result.Value.Id }, result.Value);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<TicketEvent>> GetById(string id, CancellationToken cancellationToken)
    {
        var result = await repository.GetEventAsync(id, cancellationToken);
        AddCosmosMetadata(result.RequestCharge, result.QueryScope);
        return result.Value is null ? NotFound() : Ok(result.Value);
    }

    [HttpGet("upcoming")]
    public async Task<ActionResult<IReadOnlyList<TicketEvent>>> GetUpcoming(CancellationToken cancellationToken)
    {
        var result = await repository.GetUpcomingEventsAsync(cancellationToken);
        AddCosmosMetadata(result.RequestCharge, result.QueryScope);
        return Ok(result.Value);
    }

    [HttpGet("city/{city}")]
    public async Task<ActionResult<IReadOnlyList<TicketEvent>>> GetByCity(
        string city,
        CancellationToken cancellationToken)
    {
        var result = await repository.GetEventsByCityAsync(city, cancellationToken);
        AddCosmosMetadata(result.RequestCharge, result.QueryScope);
        return Ok(result.Value);
    }

    private void AddCosmosMetadata(double requestCharge, string queryScope)
    {
        Response.Headers["x-ms-request-charge"] = requestCharge.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        Response.Headers["x-cosmos-query-scope"] = queryScope;
    }
}