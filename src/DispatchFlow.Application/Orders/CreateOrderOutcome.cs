namespace DispatchFlow.Application.Orders;

public enum CreateOrderOutcome {
    Created,
    SlotNotFound,
    InsufficientCapacity,
    Replayed,
    IdempotencyConflict
}
