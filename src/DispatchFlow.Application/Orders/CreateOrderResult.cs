namespace DispatchFlow.Application.Orders;

public sealed class CreateOrderResult {
    private CreateOrderResult(CreateOrderOutcome outcome, Order? order) {
        Outcome = outcome;
        Order = order;
    }

    public CreateOrderOutcome Outcome { get; }
    public Order? Order { get; }

    public static CreateOrderResult Created(Order order) {
        ArgumentNullException.ThrowIfNull(order);
        return new CreateOrderResult(CreateOrderOutcome.Created, order);
    }

    public static CreateOrderResult SlotNotFound() => new(CreateOrderOutcome.SlotNotFound, null);

    public static CreateOrderResult InsufficientCapacity() => new(CreateOrderOutcome.InsufficientCapacity, null);
}
