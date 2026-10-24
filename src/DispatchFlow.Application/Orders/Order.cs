namespace DispatchFlow.Application.Orders;

public sealed class Order {
    public Order(Guid id, Guid slotId, int quantity, DateTimeOffset createdAtUtc, string? idempotencyKey = null) {
        if (id == Guid.Empty)
            throw new ArgumentException("An order ID must not be empty.", nameof(id));

        if (slotId == Guid.Empty)
            throw new ArgumentException("A delivery slot ID must not be empty.", nameof(slotId));

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);

        if (createdAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("The creation time must use UTC.", nameof(createdAtUtc));

        if (idempotencyKey is not null && !IdempotencyKeyRules.IsValid(idempotencyKey))
            throw new ArgumentException("The idempotency key has an invalid length or format.", nameof(idempotencyKey));

        Id = id;
        SlotId = slotId;
        Quantity = quantity;
        CreatedAtUtc = createdAtUtc;
        Status = OrderStatus.Created;
        IdempotencyKey = idempotencyKey;
    }

    public Guid Id { get; private set; }
    public Guid SlotId { get; private set; }
    public int Quantity { get; private set; }
    public OrderStatus Status { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public string? IdempotencyKey { get; private set; }
}
