using DispatchFlow.Api.Contracts.Orders;
using DispatchFlow.Application.Orders;
using Microsoft.AspNetCore.Mvc;

namespace DispatchFlow.Api.Controllers;

[ApiController]
[Route("api/orders")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
public sealed class OrdersController(OrderService orderService) : ControllerBase {
    [HttpPost]
    [ProducesResponseType<OrderResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<OrderResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OrderResponse>> Create(CreateOrderRequest request, CancellationToken ct) {
        string? idempotencyKey = null;
        if (Request.Headers.TryGetValue("Idempotency-Key", out var keys)) {
            if (keys.Count != 1 || !IdempotencyKeyRules.IsValid(keys[0])) {
                ModelState.AddModelError("Idempotency-Key",
                    $"Provide one idempotency key of 1 to {IdempotencyKeyRules.MAX_LENGTH} characters using letters A-Z, a-z, digits, or -_.:.");
                return ValidationProblem(ModelState);
            }

            idempotencyKey = keys[0];
        }

        // ApiController validates the required nullable properties before invoking this action.
        var result = await orderService.CreateAsync(request.SlotId!.Value, request.Quantity!.Value, idempotencyKey, ct);

        switch (result.Outcome) {
            case CreateOrderOutcome.Created:
                var order = result.Order ?? throw new InvalidOperationException("A created order result must contain an order.");
                return CreatedAtAction(nameof(GetById), new { id = order.Id }, OrderResponse.FromOrder(order));
            case CreateOrderOutcome.SlotNotFound:
                return Problem(statusCode: StatusCodes.Status404NotFound, title: "Delivery slot not found.");
            case CreateOrderOutcome.InsufficientCapacity:
                return Problem(statusCode: StatusCodes.Status409Conflict, title: "Insufficient delivery slot capacity.");
            case CreateOrderOutcome.Replayed:
                return Ok(OrderResponse.FromOrder(result.Order
                    ?? throw new InvalidOperationException("A replayed order result must contain an order.")));
            case CreateOrderOutcome.IdempotencyConflict:
                return Problem(statusCode: StatusCodes.Status409Conflict, title: "Idempotency key conflict.",
                    detail: "This idempotency key was already used with a different delivery slot or quantity.");
            default:
                throw new InvalidOperationException("Unknown order creation outcome.");
        }
    }

    [HttpPost("{id}/cancel")]
    [ProducesResponseType<OrderResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OrderResponse>> Cancel(Guid id, CancellationToken ct) {
        if (id == Guid.Empty) {
            ModelState.AddModelError(nameof(id), "The order ID must not be empty.");
            return ValidationProblem(ModelState);
        }

        var result = await orderService.CancelAsync(id, ct);

        switch (result.Outcome) {
            case CancelOrderOutcome.Cancelled:
            case CancelOrderOutcome.AlreadyCancelled:
                return Ok(OrderResponse.FromOrder(result.Order
                    ?? throw new InvalidOperationException("A cancellation result must contain an order.")));
            case CancelOrderOutcome.NotFound:
                return Problem(statusCode: StatusCodes.Status404NotFound, title: "Order not found.");
            default:
                throw new InvalidOperationException("Unknown order cancellation outcome.");
        }
    }

    [HttpGet("{id}")]
    [ProducesResponseType<OrderResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OrderResponse>> GetById(Guid id, CancellationToken ct) {
        if (id == Guid.Empty) {
            ModelState.AddModelError(nameof(id), "The order ID must not be empty.");
            return ValidationProblem(ModelState);
        }

        var order = await orderService.GetByIdAsync(id, ct);
        return order is null
            ? Problem(statusCode: StatusCodes.Status404NotFound, title: "Order not found.")
            : Ok(OrderResponse.FromOrder(order));
    }
}
