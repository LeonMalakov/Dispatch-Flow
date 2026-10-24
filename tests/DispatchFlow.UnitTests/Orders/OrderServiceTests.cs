using DispatchFlow.Application.Orders;
using NSubstitute;

namespace DispatchFlow.UnitTests.Orders;

public sealed class OrderServiceTests {
    [Fact]
    public async Task CreateAsync_AssignsServerIdAndUtcTime_AndPassesRequestToRepository() {
        var now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        var repository = Substitute.For<IOrderRepository>();

        repository
            .CreateWithReservationAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>())
            .Returns(call => CreateOrderResult.Created(call.Arg<Order>()));

        var service = new OrderService(repository, new FixedTimeProvider(now));
        var slotId = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();

        var result = await service.CreateAsync(slotId, 3, cancellation.Token);

        var order = Assert.IsType<Order>(result.Order);

        Assert.NotEqual(Guid.Empty, order.Id);
        Assert.Equal(slotId, order.SlotId);
        Assert.Equal(3, order.Quantity);
        Assert.Equal(now, order.CreatedAtUtc);
        Assert.Equal(OrderStatus.Created, order.Status);
        Assert.Equal(CreateOrderOutcome.Created, result.Outcome);

        await repository.Received(1).CreateWithReservationAsync(order, cancellation.Token);
    }

    [Fact]
    public async Task CreateAsync_PassesIdempotencyKeyAndCancellationToRepository() {
        var repository = Substitute.For<IOrderRepository>();

        repository
            .CreateWithReservationAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>())
            .Returns(call => CreateOrderResult.Created(call.Arg<Order>()));

        var service = new OrderService(repository, new FixedTimeProvider(DateTimeOffset.UtcNow));
        var slotId = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();

        var result = await service.CreateAsync(slotId, 2, "Order:AbC-123", cancellation.Token);

        var order = Assert.IsType<Order>(result.Order);

        Assert.Equal("Order:AbC-123", order.IdempotencyKey);

        await repository.Received(1).CreateWithReservationAsync(order, cancellation.Token);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
