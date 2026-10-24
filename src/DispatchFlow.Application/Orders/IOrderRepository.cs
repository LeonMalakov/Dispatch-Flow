namespace DispatchFlow.Application.Orders;

public interface IOrderRepository {
    /// <summary>
    /// Reserves slot capacity and persists the order atomically.
    /// Unexpected storage errors propagate to the caller.
    /// </summary>
    Task<CreateOrderResult> CreateWithReservationAsync(Order order, CancellationToken ct = default);

    Task<Order?> GetByIdAsync(Guid orderId, CancellationToken ct = default);

    /// <summary>
    /// Cancels a created order and releases its reservation atomically.
    /// Repeated cancellation does not release capacity again.
    /// </summary>
    Task<CancelOrderResult> CancelWithReleaseAsync(Guid orderId, CancellationToken ct = default);
}
