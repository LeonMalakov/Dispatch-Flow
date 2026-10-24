using DispatchFlow.Application.DeliverySlots;

namespace DispatchFlow.UnitTests.DeliverySlots;

public sealed class DeliverySlotTests {
    private static readonly DateTimeOffset StartsAtUtc = new(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset EndsAtUtc = StartsAtUtc.AddHours(2);

    [Fact]
    public void Constructor_CreatesSlotWithoutReservations() {
        // Arrange.
        var id = Guid.NewGuid();

        // Act.
        var slot = new DeliverySlot(id, 10, StartsAtUtc, EndsAtUtc);

        // Assert.
        Assert.Equal(id, slot.Id);
        Assert.Equal(10, slot.Capacity);
        Assert.Equal(0, slot.Reserved);
        Assert.Equal(StartsAtUtc, slot.StartsAtUtc);
        Assert.Equal(EndsAtUtc, slot.EndsAtUtc);
    }

    [Fact]
    public void Constructor_RejectsEmptyId() {
        Assert.Throws<ArgumentException>(
            "id",
            () => new DeliverySlot(Guid.Empty, 10, StartsAtUtc, EndsAtUtc));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Constructor_RejectsNonPositiveCapacity(int capacity) {
        Assert.Throws<ArgumentOutOfRangeException>(
            "capacity",
            () => new DeliverySlot(Guid.NewGuid(), capacity, StartsAtUtc, EndsAtUtc));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsEndAtOrBeforeStart(int endOffsetHours) {
        Assert.Throws<ArgumentException>(
            "endsAtUtc",
            () => new DeliverySlot(Guid.NewGuid(), 10, StartsAtUtc, StartsAtUtc.AddHours(endOffsetHours)));
    }

    [Theory]
    [InlineData(-3)]
    [InlineData(3)]
    public void Constructor_RejectsNonUtcStart(int offsetHours) {
        var start = StartsAtUtc.ToOffset(TimeSpan.FromHours(offsetHours));

        Assert.Throws<ArgumentException>(
            "startsAtUtc",
            () => new DeliverySlot(Guid.NewGuid(), 10, start, EndsAtUtc));
    }

    [Theory]
    [InlineData(-3)]
    [InlineData(3)]
    public void Constructor_RejectsNonUtcEnd(int offsetHours) {
        var end = EndsAtUtc.ToOffset(TimeSpan.FromHours(offsetHours));

        Assert.Throws<ArgumentException>(
            "endsAtUtc",
            () => new DeliverySlot(Guid.NewGuid(), 10, StartsAtUtc, end));
    }
}
