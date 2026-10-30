using Microsoft.Extensions.Logging;

namespace DispatchFlow.Application.Orders;

public sealed class OrderService(
    IOrderRepository orderRepository, TimeProvider timeProvider, OrderMetrics metrics, ILogger<OrderService> logger) {
    public Task<CreateOrderResult> CreateAsync(Guid slotId, int quantity, CancellationToken ct = default) =>
        CreateAsync(slotId, quantity, null, ct);

    public async Task<CreateOrderResult> CreateAsync(Guid slotId, int quantity, string? idempotencyKey, CancellationToken ct = default) {
        var order = new Order(Guid.NewGuid(), slotId, quantity, timeProvider.GetUtcNow(), idempotencyKey);
        var result = await orderRepository.CreateWithReservationAsync(order, ct);

        switch (result.Outcome) {
            case CreateOrderOutcome.Created:
                metrics.OrderCreated();
                logger.LogInformation("Order {OrderId} created for slot {SlotId} with quantity {Quantity}.",
                    result.Order!.Id, result.Order.SlotId, result.Order.Quantity);
                break;
            case CreateOrderOutcome.Replayed:
                metrics.IdempotencyReplayed();
                logger.LogInformation("Existing order {OrderId} returned by idempotent replay for slot {SlotId} with quantity {Quantity}.",
                    result.Order!.Id, result.Order.SlotId, result.Order.Quantity);
                break;
            case CreateOrderOutcome.InsufficientCapacity:
                metrics.CapacityRejected();
                logger.LogInformation("Order creation rejected due to insufficient capacity in slot {SlotId} for quantity {Quantity}.",
                    slotId, quantity);
                break;
        }

        return result;
    }

    public async Task<Order?> GetByIdAsync(Guid orderId, CancellationToken ct = default) {
        var order = await orderRepository.GetByIdAsync(orderId, ct);
        return order;
    }

    public async Task<CancelOrderResult> CancelAsync(Guid orderId, CancellationToken ct = default) {
        var result = await orderRepository.CancelWithReleaseAsync(orderId, ct);

        switch (result.Outcome) {
            case CancelOrderOutcome.Cancelled:
                metrics.OrderCancelled();
                logger.LogInformation("Order {OrderId} cancelled for slot {SlotId} with quantity {Quantity}.",
                    result.Order!.Id, result.Order.SlotId, result.Order.Quantity);
                break;
            case CancelOrderOutcome.AlreadyCancelled:
                logger.LogInformation("Order {OrderId} was already cancelled.", orderId);
                break;
        }

        return result;
    }
}
