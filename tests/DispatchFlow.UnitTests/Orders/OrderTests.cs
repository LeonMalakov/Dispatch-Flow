using DispatchFlow.Application.Orders;

namespace DispatchFlow.UnitTests.Orders;

public sealed class OrderTests {
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void Constructor_CreatesActiveOrder(int quantity) {
        // Arrange.
        var id = Guid.NewGuid();
        var slotId = Guid.NewGuid();

        // Act.
        var order = new Order(id, slotId, quantity, CreatedAtUtc);

        // Assert.
        Assert.Equal(id, order.Id);
        Assert.Equal(slotId, order.SlotId);
        Assert.Equal(quantity, order.Quantity);
        Assert.Equal(CreatedAtUtc, order.CreatedAtUtc);
        Assert.Equal(OrderStatus.Created, order.Status);
        Assert.Null(order.IdempotencyKey);
    }

    [Fact]
    public void Constructor_RejectsEmptyId() {
        Assert.Throws<ArgumentException>(
            "id",
            () => new Order(Guid.Empty, Guid.NewGuid(), 1, CreatedAtUtc));
    }

    [Fact]
    public void Constructor_RejectsEmptySlotId() {
        Assert.Throws<ArgumentException>(
            "slotId",
            () => new Order(Guid.NewGuid(), Guid.Empty, 1, CreatedAtUtc));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Constructor_RejectsNonPositiveQuantity(int quantity) {
        Assert.Throws<ArgumentOutOfRangeException>(
            "quantity",
            () => new Order(Guid.NewGuid(), Guid.NewGuid(), quantity, CreatedAtUtc));
    }

    [Theory]
    [InlineData(-3)]
    [InlineData(3)]
    public void Constructor_RejectsNonUtcCreationTime(int offsetHours) {
        var createdAt = CreatedAtUtc.ToOffset(TimeSpan.FromHours(offsetHours));

        Assert.Throws<ArgumentException>(
            "createdAtUtc",
            () => new Order(Guid.NewGuid(), Guid.NewGuid(), 1, createdAt));
    }

    public static TheoryData<string?> ValidIdempotencyKeys => new() {
        null,
        "A",
        "Az09-_.:",
        new string('a', 128)
    };

    public static TheoryData<string> InvalidIdempotencyKeys => new() {
        "",
        " ",
        " key",
        "key ",
        "key value",
        "key,value",
        "key/value",
        "ключ",
        "key\n",
        "key\t",
        new string('a', 129)
    };

    [Theory]
    [MemberData(nameof(ValidIdempotencyKeys))]
    public void Constructor_PreservesValidIdempotencyKey(string? key) {
        // Act.
        var order = new Order(Guid.NewGuid(), Guid.NewGuid(), 1, CreatedAtUtc, key);

        // Assert.
        Assert.Equal(key, order.IdempotencyKey);
    }

    [Theory]
    [MemberData(nameof(InvalidIdempotencyKeys))]
    public void Constructor_RejectsInvalidIdempotencyKey(string key) {
        Assert.Throws<ArgumentException>(
            "idempotencyKey",
            () => new Order(Guid.NewGuid(), Guid.NewGuid(), 1, CreatedAtUtc, key));
    }
}
