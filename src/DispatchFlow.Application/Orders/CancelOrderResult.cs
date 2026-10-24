namespace DispatchFlow.Application.Orders;

public sealed class CancelOrderResult {
    private CancelOrderResult(CancelOrderOutcome outcome, Order? order) {
        Outcome = outcome;
        Order = order;
    }

    public CancelOrderOutcome Outcome { get; }
    public Order? Order { get; }

    public static CancelOrderResult Cancelled(Order order) {
        ArgumentNullException.ThrowIfNull(order);
        return new CancelOrderResult(CancelOrderOutcome.Cancelled, order);
    }

    public static CancelOrderResult AlreadyCancelled(Order order) {
        ArgumentNullException.ThrowIfNull(order);
        return new CancelOrderResult(CancelOrderOutcome.AlreadyCancelled, order);
    }

    public static CancelOrderResult NotFound() => new(CancelOrderOutcome.NotFound, null);
}
