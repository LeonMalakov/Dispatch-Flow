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
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OrderResponse>> Create(CreateOrderRequest request, CancellationToken ct) {
        // ApiController validates the required nullable properties before invoking this action.
        var result = await orderService.CreateAsync(request.SlotId!.Value, request.Quantity!.Value, ct);

        switch (result.Outcome) {
            case CreateOrderOutcome.Created:
                var order = result.Order ?? throw new InvalidOperationException("A created order result must contain an order.");
                return CreatedAtAction(nameof(GetById), new { id = order.Id }, OrderResponse.FromOrder(order));
            case CreateOrderOutcome.SlotNotFound:
                return Problem(statusCode: StatusCodes.Status404NotFound, title: "Delivery slot not found.");
            case CreateOrderOutcome.InsufficientCapacity:
                return Problem(statusCode: StatusCodes.Status409Conflict, title: "Insufficient delivery slot capacity.");
            default:
                throw new InvalidOperationException("Unknown order creation outcome.");
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
