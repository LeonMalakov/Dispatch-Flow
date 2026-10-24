using DispatchFlow.Application.DeliverySlots;
using DispatchFlow.Application.Orders;
using DispatchFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DispatchFlow.IntegrationTests.Persistence;

[Collection("PostgreSQL")]
public sealed class OrderTransactionTests(PostgreSqlFixture postgres) {
    [Fact]
    public async Task CreateWithReservationAsync_WhenOrderIdIsDuplicate_RollsBackCapacityReservation() {
        await using var database = await postgres.CreateDatabaseAsync();
        var now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        var originalSlot = new DeliverySlot(Guid.NewGuid(), 10, now.AddDays(1), now.AddDays(1).AddHours(4));
        var targetSlot = new DeliverySlot(Guid.NewGuid(), 10, now.AddDays(1), now.AddDays(1).AddHours(4));
        var existingOrder = new Order(Guid.NewGuid(), originalSlot.Id, 2, now);

        await using (var setup = database.CreateContext()) {
            setup.DeliverySlots.AddRange(originalSlot, targetSlot);
            await setup.SaveChangesAsync();

            var result = await new EfOrderRepository(setup).CreateWithReservationAsync(existingOrder);

            Assert.Equal(CreateOrderOutcome.Created, result.Outcome);
        }

        // Use a fresh context so the duplicate ID reaches PostgreSQL, not EF's identity map.
        await using (var context = database.CreateContext()) {
            var duplicate = new Order(existingOrder.Id, targetSlot.Id, 4, now.AddMinutes(1));

            var error = await Assert.ThrowsAsync<DbUpdateException>(() =>
                new EfOrderRepository(context).CreateWithReservationAsync(duplicate));
            var postgresError = Assert.IsType<PostgresException>(error.InnerException);

            Assert.Equal(PostgresErrorCodes.UniqueViolation, postgresError.SqlState);
            Assert.Equal("pk_orders", postgresError.ConstraintName);
        }

        await using (var reader = database.CreateContext()) {
            Assert.Equal(
                0,
                (await reader.DeliverySlots.SingleAsync(slot => slot.Id == targetSlot.Id)).Reserved);
            Assert.Equal(
                2,
                (await reader.DeliverySlots.SingleAsync(slot => slot.Id == originalSlot.Id)).Reserved);

            var storedOrder = Assert.Single(await reader.Orders.ToListAsync());

            Assert.Equal(existingOrder.Id, storedOrder.Id);
            Assert.Equal(originalSlot.Id, storedOrder.SlotId);
            Assert.Equal(2, storedOrder.Quantity);
            Assert.Equal(now, storedOrder.CreatedAtUtc);
            Assert.Equal(OrderStatus.Created, storedOrder.Status);
        }

        // The full capacity must still be available after the failed transaction.
        await using (var context = database.CreateContext()) {
            var nextOrder = new Order(Guid.NewGuid(), targetSlot.Id, 10, now.AddMinutes(2));
            var result = await new EfOrderRepository(context).CreateWithReservationAsync(nextOrder);

            Assert.Equal(CreateOrderOutcome.Created, result.Outcome);
        }

        await using var finalReader = database.CreateContext();
        Assert.Equal(
            10,
            (await finalReader.DeliverySlots.SingleAsync(slot => slot.Id == targetSlot.Id)).Reserved);
        Assert.Equal(2, await finalReader.Orders.CountAsync());
    }
}
