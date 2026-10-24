namespace DispatchFlow.Application.Orders;

public sealed class OrderService(IOrderRepository orderRepository, TimeProvider timeProvider) {
    public Task<CreateOrderResult> CreateAsync(Guid slotId, int quantity, CancellationToken ct = default) =>
        CreateAsync(slotId, quantity, null, ct);

    public Task<CreateOrderResult> CreateAsync(Guid slotId, int quantity, string? idempotencyKey, CancellationToken ct = default) {
        var order = new Order(Guid.NewGuid(), slotId, quantity, timeProvider.GetUtcNow(), idempotencyKey);
        return orderRepository.CreateWithReservationAsync(order, ct);
    }

    public Task<Order?> GetByIdAsync(Guid orderId, CancellationToken ct = default) =>
        orderRepository.GetByIdAsync(orderId, ct);
}
