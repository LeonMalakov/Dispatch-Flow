using System.Diagnostics.Metrics;

namespace DispatchFlow.Application.Orders;

public sealed class OrderMetrics {
    public const string METER_NAME = "DispatchFlow.Orders";

    private readonly Counter<long> _created;
    private readonly Counter<long> _cancelled;
    private readonly Counter<long> _idempotencyReplayed;
    private readonly Counter<long> _capacityRejected;

    public OrderMetrics(IMeterFactory meterFactory) {
        var meter = meterFactory.Create(new MeterOptions(METER_NAME));
        _created = meter.CreateCounter<long>(
            "dispatchflow.orders.created", description: "Successfully committed new orders.");
        _cancelled = meter.CreateCounter<long>(
            "dispatchflow.orders.cancelled", description: "Successfully committed order cancellations.");
        _idempotencyReplayed = meter.CreateCounter<long>(
            "dispatchflow.orders.idempotency_replayed", description: "Requests returning an existing order through idempotency.");
        _capacityRejected = meter.CreateCounter<long>(
            "dispatchflow.orders.capacity_rejected", description: "Order creation requests rejected due to insufficient capacity.");
    }

    public void OrderCreated() => _created.Add(1);

    public void OrderCancelled() => _cancelled.Add(1);

    public void IdempotencyReplayed() => _idempotencyReplayed.Add(1);

    public void CapacityRejected() => _capacityRejected.Add(1);
}
