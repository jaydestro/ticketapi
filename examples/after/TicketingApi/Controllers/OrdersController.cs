using Microsoft.AspNetCore.Mvc;
using TicketingApi.Models;
using TicketingApi.Pagination;
using TicketingApi.Repositories;

namespace TicketingApi.Controllers;

[ApiController]
[Route("api/orders")]
public sealed class OrdersController(ITicketingRepository repository) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<Order>> Purchase(
        PurchaseTicketsRequest request,
        [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid idempotency key",
                Detail = "Idempotency-Key is required and must be at most 200 characters.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        try
        {
            var result = await repository.PurchaseTicketsAsync(request, idempotencyKey.Trim(), cancellationToken);
            AddCosmosMetadata(result.RequestCharge, result.QueryScope);
            return result.Value is null ? NotFound() : CreatedAtAction(
                nameof(GetByEvent),
                new { eventId = result.Value.EventId },
                result.Value);
        }
        catch (TicketsUnavailableException exception)
        {
            return Conflict(new ProblemDetails
            {
                Title = "Tickets unavailable",
                Detail = exception.Message,
                Status = StatusCodes.Status409Conflict
            });
        }
        catch (IdempotencyConflictException exception)
        {
            return Conflict(new ProblemDetails
            {
                Title = "Idempotency key conflict",
                Detail = exception.Message,
                Status = StatusCodes.Status409Conflict
            });
        }
    }

    [HttpGet("customer/{customerId}")]
    public async Task<ActionResult<IReadOnlyList<Order>>> GetByCustomer(
        string customerId,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? continuationToken = null,
        CancellationToken cancellationToken = default)
    {
        var result = await repository.GetOrdersByCustomerAsync(
            customerId,
            ClampPageSize(pageSize),
            ContinuationTokenCodec.Decode(continuationToken),
            cancellationToken);
        AddCosmosMetadata(result.RequestCharge, result.QueryScope);
        AddContinuationToken(result.ContinuationToken);
        return Ok(result.Items);
    }

    [HttpGet("event/{eventId}")]
    public async Task<ActionResult<IReadOnlyList<Order>>> GetByEvent(
        string eventId,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? continuationToken = null,
        CancellationToken cancellationToken = default)
    {
        var result = await repository.GetOrdersByEventAsync(
            eventId,
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