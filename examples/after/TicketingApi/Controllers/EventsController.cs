using Microsoft.AspNetCore.Mvc;
using TicketingApi.Models;
using TicketingApi.Pagination;
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
        if (request.EventDate <= DateTimeOffset.UtcNow)
        {
            ModelState.AddModelError(nameof(request.EventDate), "EventDate must be in the future.");
            return ValidationProblem(ModelState);
        }

        var ticketEvent = new TicketEvent
        {
            Name = request.Name.Trim(),
            Venue = request.Venue.Trim(),
            City = request.City.Trim(),
            EventDate = request.EventDate.UtcDateTime,
            TotalSeats = request.TotalSeats,
            AvailableSeats = request.TotalSeats,
            PriceTier = request.PriceTier.ToLowerInvariant()
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
    public async Task<ActionResult<IReadOnlyList<TicketEvent>>> GetUpcoming(
        [FromQuery] int pageSize = 50,
        [FromQuery] string? continuationToken = null,
        CancellationToken cancellationToken = default)
    {
        var result = await repository.GetUpcomingEventsAsync(
            ClampPageSize(pageSize),
            ContinuationTokenCodec.Decode(continuationToken),
            cancellationToken);
        AddCosmosMetadata(result.RequestCharge, result.QueryScope);
        AddContinuationToken(result.ContinuationToken);
        return Ok(result.Items);
    }

    [HttpGet("city/{city}")]
    public async Task<ActionResult<IReadOnlyList<TicketEvent>>> GetByCity(
        string city,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? continuationToken = null,
        CancellationToken cancellationToken = default)
    {
        var result = await repository.GetEventsByCityAsync(
            city,
            ClampPageSize(pageSize),
            ContinuationTokenCodec.Decode(continuationToken),
            cancellationToken);
        AddCosmosMetadata(result.RequestCharge, result.QueryScope);
        AddContinuationToken(result.ContinuationToken);
        return Ok(result.Items);
    }

    private void AddCosmosMetadata(double requestCharge, string queryScope)
    {
        Response.Headers["x-ms-request-charge"] = requestCharge.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        Response.Headers["x-cosmos-query-scope"] = queryScope;
    }

    private void AddContinuationToken(string? token)
    {
        var encoded = ContinuationTokenCodec.Encode(token);
        if (encoded is not null)
        {
            Response.Headers["x-continuation-token"] = encoded;
        }
    }

    private static int ClampPageSize(int pageSize) => Math.Clamp(pageSize, 1, 100);
}