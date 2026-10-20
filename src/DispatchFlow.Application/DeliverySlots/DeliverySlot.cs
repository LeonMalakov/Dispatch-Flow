namespace DispatchFlow.Application.DeliverySlots;

public sealed class DeliverySlot {
    public DeliverySlot(Guid id, int capacity, DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc) {
        if (id == Guid.Empty)
            throw new ArgumentException("A delivery slot ID must not be empty.", nameof(id));

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        if (startsAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("The start time must use UTC.", nameof(startsAtUtc));

        if (endsAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("The end time must use UTC.", nameof(endsAtUtc));

        if (startsAtUtc >= endsAtUtc)
            throw new ArgumentException("The end time must be later than the start time.", nameof(endsAtUtc));

        Id = id;
        Capacity = capacity;
        StartsAtUtc = startsAtUtc;
        EndsAtUtc = endsAtUtc;
    }

    public Guid Id { get; private set; }
    public int Capacity { get; private set; }
    public int Reserved { get; private set; }
    public DateTimeOffset StartsAtUtc { get; private set; }
    public DateTimeOffset EndsAtUtc { get; private set; }
}