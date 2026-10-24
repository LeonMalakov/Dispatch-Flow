using DispatchFlow.Application.Orders;

namespace DispatchFlow.Api.Contracts.Orders;

public sealed record OrderResponse(Guid Id, Guid SlotId, int Quantity, string Status, DateTimeOffset CreatedAtUtc) {
    public static OrderResponse FromOrder(Order order) =>
        new(order.Id, order.SlotId, order.Quantity, ToApiStatus(order.Status), order.CreatedAtUtc);

    private static string ToApiStatus(OrderStatus status) => status switch {
        OrderStatus.Created => "Created",
        OrderStatus.Cancelled => "Cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };
}
