using Microsoft.AspNetCore.Mvc;
using TicketingApi.Models;
using TicketingApi.Repositories;

namespace TicketingApi.Controllers;

[ApiController]
[Route("api/orders")]
public sealed class OrdersController(ITicketingRepository repository) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<Order>> Purchase(
        PurchaseTicketsRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await repository.PurchaseTicketsAsync(request, cancellationToken);
            AddRequestCharge(result.RequestCharge);
            return result.Value is null ? NotFound() : CreatedAtAction(
                nameof(GetByEvent),
                new { eventId = result.Value.EventId },
                result.Value);
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new ProblemDetails
            {
                Title = "Tickets unavailable",
                Detail = exception.Message,
                Status = StatusCodes.Status409Conflict
            });
        }
    }

    [HttpGet("customer/{customerId}")]
    public async Task<ActionResult<IReadOnlyList<Order>>> GetByCustomer(
        string customerId,
        CancellationToken cancellationToken)
    {
        var result = await repository.GetOrdersByCustomerAsync(customerId, cancellationToken);
        AddRequestCharge(result.RequestCharge);
        return Ok(result.Value);
    }

    [HttpGet("event/{eventId}")]
    public async Task<ActionResult<IReadOnlyList<Order>>> GetByEvent(
        string eventId,
        CancellationToken cancellationToken)
    {
        var result = await repository.GetOrdersByEventAsync(eventId, cancellationToken);
        AddRequestCharge(result.RequestCharge);
        return Ok(result.Value);
    }

    private void AddRequestCharge(double requestCharge) =>
        Response.Headers["x-ms-request-charge"] = requestCharge.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
}