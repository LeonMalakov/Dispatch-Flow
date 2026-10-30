using System.Diagnostics.Metrics;
using DispatchFlow.Application.Orders;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace DispatchFlow.UnitTests.Orders;

public sealed class OrderServiceTests {
    [Fact]
    public async Task CreateAsync_AssignsServerIdAndUtcTime_AndPassesRequestToRepository() {
        // Arrange.
        var now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        var repository = Substitute.For<IOrderRepository>();

        repository
            .CreateWithReservationAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>())
            .Returns(call => CreateOrderResult.Created(call.Arg<Order>()));

        using var meter = new Meter(OrderMetrics.METER_NAME);
        var service = CreateService(repository, new FixedTimeProvider(now), meter);
        var slotId = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();

        // Act.
        var result = await service.CreateAsync(slotId, 3, cancellation.Token);

        // Assert.
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
        // Arrange.
        var repository = Substitute.For<IOrderRepository>();

        repository
            .CreateWithReservationAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>())
            .Returns(call => CreateOrderResult.Created(call.Arg<Order>()));

        using var meter = new Meter(OrderMetrics.METER_NAME);
        var service = CreateService(repository, new FixedTimeProvider(DateTimeOffset.UtcNow), meter);
        var slotId = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();

        // Act.
        var result = await service.CreateAsync(slotId, 2, "Order:AbC-123", cancellation.Token);

        // Assert.
        var order = Assert.IsType<Order>(result.Order);

        Assert.Equal("Order:AbC-123", order.IdempotencyKey);

        await repository.Received(1).CreateWithReservationAsync(order, cancellation.Token);
    }

    [Fact]
    public async Task CreateAsync_RecordsCreationOnlyAfterRepositoryCompletes() {
        // Arrange.
        var repository = Substitute.For<IOrderRepository>();
        var completion = new TaskCompletionSource<CreateOrderResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.CreateWithReservationAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>()).Returns(completion.Task);
        using var meter = new Meter(OrderMetrics.METER_NAME);
        long created = 0;
        using var listener = new MeterListener {
            InstrumentPublished = (instrument, observer) => {
                if (ReferenceEquals(instrument.Meter, meter)) {
                    observer.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => {
            if (instrument.Name == "dispatchflow.orders.created") {
                Interlocked.Add(ref created, value);
            }
        });
        listener.Start();
        var service = CreateService(repository, TimeProvider.System, meter);
        var order = new Order(Guid.NewGuid(), Guid.NewGuid(), 2, DateTimeOffset.UtcNow);

        // Act & Assert.
        var pending = service.CreateAsync(order.SlotId, order.Quantity);
        Assert.False(pending.IsCompleted);
        Assert.Equal(0, Interlocked.Read(ref created));

        completion.SetResult(CreateOrderResult.Created(order));
        await pending;
        Assert.Equal(1, Interlocked.Read(ref created));
    }

    private static OrderService CreateService(IOrderRepository repository, TimeProvider timeProvider, Meter meter) {
        var meterFactory = Substitute.For<IMeterFactory>();
        meterFactory.Create(Arg.Any<MeterOptions>()).Returns(meter);
        return new OrderService(repository, timeProvider, new OrderMetrics(meterFactory), NullLogger<OrderService>.Instance);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
