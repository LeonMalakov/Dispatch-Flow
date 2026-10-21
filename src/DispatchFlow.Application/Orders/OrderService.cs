namespace DispatchFlow.Application.Orders;

public sealed class OrderService(IOrderRepository orderRepository, TimeProvider timeProvider) {
    public Task<CreateOrderResult> CreateAsync(Guid slotId, int quantity, CancellationToken ct = default) {
        var order = new Order(Guid.NewGuid(), slotId, quantity, timeProvider.GetUtcNow());
        return orderRepository.CreateWithReservationAsync(order, ct);
    }

    public Task<Order?> GetByIdAsync(Guid orderId, CancellationToken ct = default) =>
        orderRepository.GetByIdAsync(orderId, ct);
}
