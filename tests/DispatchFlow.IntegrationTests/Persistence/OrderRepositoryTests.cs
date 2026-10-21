using DispatchFlow.Application.DeliverySlots;
using DispatchFlow.Application.Orders;
using DispatchFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DispatchFlow.IntegrationTests.Persistence;

[Collection("PostgreSQL")]
public sealed class OrderRepositoryTests(PostgreSqlFixture postgres) {
    private static readonly DateTimeOffset StartsAtUtc = new(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreateWithReservationAsync_PersistsOrderAndReservedCapacity() {
        await using var database = await postgres.CreateDatabaseAsync();
        var slot = await InsertSlotAsync(database, 10);
        var order = new Order(Guid.NewGuid(), slot.Id, 3, StartsAtUtc.AddDays(-1));

        await using (var context = database.CreateContext()) {
            var result = await new EfOrderRepository(context).CreateWithReservationAsync(order);
            Assert.Equal(CreateOrderOutcome.Created, result.Outcome);
            Assert.Same(order, result.Order);
        }

        await using var reader = database.CreateContext();
        var storedOrder = await new EfOrderRepository(reader).GetByIdAsync(order.Id);
        Assert.NotNull(storedOrder);
        Assert.Equal(slot.Id, storedOrder.SlotId);
        Assert.Equal(3, storedOrder.Quantity);
        Assert.Equal(order.CreatedAtUtc, storedOrder.CreatedAtUtc);
        Assert.Equal(OrderStatus.Created, storedOrder.Status);
        Assert.Equal(3, (await reader.DeliverySlots.AsNoTracking().SingleAsync()).Reserved);
        Assert.Null(await new EfOrderRepository(reader).GetByIdAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task CreateWithReservationAsync_WhenSlotIsMissing_DoesNotCreateOrder() {
        await using var database = await postgres.CreateDatabaseAsync();
        await using (var context = database.CreateContext()) {
            var order = new Order(Guid.NewGuid(), Guid.NewGuid(), 1, StartsAtUtc);
            var result = await new EfOrderRepository(context).CreateWithReservationAsync(order);
            Assert.Equal(CreateOrderOutcome.SlotNotFound, result.Outcome);
            Assert.Null(result.Order);
        }

        await using var reader = database.CreateContext();
        Assert.Empty(await reader.Orders.ToListAsync());
        Assert.Empty(await reader.DeliverySlots.ToListAsync());
    }

    [Theory]
    [InlineData(10)]
    [InlineData(int.MaxValue)]
    public async Task CreateWithReservationAsync_RespectsRemainingCapacity_WithoutOverflow(int capacity) {
        await using var database = await postgres.CreateDatabaseAsync();
        var slot = await InsertSlotAsync(database, capacity);

        await using (var context = database.CreateContext()) {
            var existing = new Order(Guid.NewGuid(), slot.Id, capacity - 1, StartsAtUtc);
            var result = await new EfOrderRepository(context).CreateWithReservationAsync(existing);
            Assert.Equal(CreateOrderOutcome.Created, result.Outcome);
        }

        await using (var context = database.CreateContext()) {
            var rejected = new Order(Guid.NewGuid(), slot.Id, 2, StartsAtUtc);
            var result = await new EfOrderRepository(context).CreateWithReservationAsync(rejected);
            Assert.Equal(CreateOrderOutcome.InsufficientCapacity, result.Outcome);
            Assert.Null(result.Order);
        }

        await using (var reader = database.CreateContext()) {
            Assert.Equal(capacity - 1, (await reader.DeliverySlots.SingleAsync()).Reserved);
            Assert.Equal(1, await reader.Orders.CountAsync());
        }

        await using (var context = database.CreateContext()) {
            var last = new Order(Guid.NewGuid(), slot.Id, 1, StartsAtUtc);
            var result = await new EfOrderRepository(context).CreateWithReservationAsync(last);
            Assert.Equal(CreateOrderOutcome.Created, result.Outcome);
        }

        await using (var context = database.CreateContext()) {
            var rejected = new Order(Guid.NewGuid(), slot.Id, 1, StartsAtUtc);
            var result = await new EfOrderRepository(context).CreateWithReservationAsync(rejected);
            Assert.Equal(CreateOrderOutcome.InsufficientCapacity, result.Outcome);
        }

        await using var finalReader = database.CreateContext();
        Assert.Equal(capacity, (await finalReader.DeliverySlots.SingleAsync()).Reserved);
        Assert.Equal(2, await finalReader.Orders.CountAsync());
        Assert.Equal(capacity, await finalReader.Orders.SumAsync(order => order.Quantity));
    }

    private static async Task<DeliverySlot> InsertSlotAsync(TestDatabase database, int capacity) {
        var slot = new DeliverySlot(Guid.NewGuid(), capacity, StartsAtUtc, StartsAtUtc.AddHours(4));
        await using var context = database.CreateContext();
        context.DeliverySlots.Add(slot);
        await context.SaveChangesAsync();
        return slot;
    }
}
