using DispatchFlow.Application.DeliverySlots;
using DispatchFlow.Application.Orders;
using DispatchFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DispatchFlow.IntegrationTests.Persistence;

[Collection("PostgreSQL")]
public sealed class OrderCancellationTests(PostgreSqlFixture postgres) {
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Cancel_ReturnsFreshCancelledOrder_WhenContextTracksCreatedVersion() {
        // Arrange.
        await using var database = await postgres.CreateDatabaseAsync();
        var orders = await CreateOrdersAsync(database, 10, 2, 3);
        var target = orders[0];
        await using var context = database.CreateContext();
        var tracked = await context.Orders.SingleAsync(order => order.Id == target.Id);
        var repository = new EfOrderRepository(context);

        // Act.
        var result = await repository.CancelWithReleaseAsync(target.Id);

        // Assert.
        Assert.Equal(CancelOrderOutcome.Cancelled, result.Outcome);
        var cancelled = Assert.IsType<Order>(result.Order);

        Assert.Equal(target.Id, cancelled.Id);
        Assert.Equal(target.SlotId, cancelled.SlotId);
        Assert.Equal(target.Quantity, cancelled.Quantity);
        Assert.Equal(target.CreatedAtUtc, cancelled.CreatedAtUtc);
        Assert.Equal(OrderStatus.Cancelled, cancelled.Status);
        Assert.NotSame(tracked, cancelled);
        Assert.Equal(OrderStatus.Created, tracked.Status);
        Assert.Null(context.Database.CurrentTransaction);
        Assert.Equal(0, await context.SaveChangesAsync());

        await using var reader = database.CreateContext();
        var stored = await reader.Orders.AsNoTracking().ToListAsync();

        Assert.Equal(OrderStatus.Cancelled, stored.Single(order => order.Id == target.Id).Status);
        Assert.Equal(OrderStatus.Created, stored.Single(order => order.Id == orders[1].Id).Status);
        Assert.Equal(3, (await reader.DeliverySlots.SingleAsync()).Reserved);
    }

    [Fact]
    public async Task Cancel_WhenReservedIsInsufficient_RollsBackStatus_AndAllowsRetryAfterRepair() {
        // Arrange.
        await using var database = await postgres.CreateDatabaseAsync();
        var target = Assert.Single(await CreateOrdersAsync(database, 2, 2));

        // A value valid under the row CHECK, but inconsistent with the order's reservation.
        await database.ExecuteAsync(
            "UPDATE delivery_slots SET reserved = 1 WHERE id = @slot",
            new NpgsqlParameter("slot", target.SlotId));
        await using var context = database.CreateContext();
        var repository = new EfOrderRepository(context);

        // Act.
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.CancelWithReleaseAsync(target.Id));

        // Assert.
        Assert.Null(context.Database.CurrentTransaction);
        Assert.Empty(context.ChangeTracker.Entries());

        await using (var reader = database.CreateContext()) {
            Assert.Equal(OrderStatus.Created, (await reader.Orders.SingleAsync()).Status);
            Assert.Equal(1, (await reader.DeliverySlots.SingleAsync()).Reserved);
        }

        // Repair the test data and reuse the same context after rollback.
        await database.ExecuteAsync(
            "UPDATE delivery_slots SET reserved = 2 WHERE id = @slot",
            new NpgsqlParameter("slot", target.SlotId));
        var retry = await repository.CancelWithReleaseAsync(target.Id);

        Assert.Equal(CancelOrderOutcome.Cancelled, retry.Outcome);
        Assert.Equal(OrderStatus.Cancelled, Assert.IsType<Order>(retry.Order).Status);

        await using var finalReader = database.CreateContext();
        Assert.Equal(OrderStatus.Cancelled, (await finalReader.Orders.SingleAsync()).Status);
        Assert.Equal(0, (await finalReader.DeliverySlots.SingleAsync()).Reserved);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancel_WhenAnotherCancellationIsUncommitted_RechecksStatusAfterWaiting(bool commitWinner) {
        // Arrange.
        await using var database = await postgres.CreateDatabaseAsync();
        var orders = await CreateOrdersAsync(database, 10, 2, 3);
        var target = orders[0];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;

        await using var writer = database.CreateContext();
        await using var transaction = await writer.Database.BeginTransactionAsync(ct);
        await writer.Orders
            .Where(order => order.Id == target.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(order => order.Status, OrderStatus.Cancelled), ct);
        await writer.DeliverySlots
            .Where(slot => slot.Id == target.SlotId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(slot => slot.Reserved, slot => slot.Reserved - target.Quantity), ct);

        int blockingPid = ((NpgsqlConnection)writer.Database.GetDbConnection()).ProcessID;
        await using var contender = database.CreateContext();

        // Act.
        var pending = new EfOrderRepository(contender).CancelWithReleaseAsync(target.Id, ct);
        await WaitForBlockedUpdateAsync(database, blockingPid, ct);
        Assert.False(pending.IsCompleted);

        if (commitWinner) {
            await transaction.CommitAsync(ct);
        } else {
            await transaction.RollbackAsync(ct);
        }

        var result = await pending;

        // Assert.
        Assert.Equal(
            commitWinner ? CancelOrderOutcome.AlreadyCancelled : CancelOrderOutcome.Cancelled,
            result.Outcome);
        var cancelled = Assert.IsType<Order>(result.Order);

        Assert.Equal(target.Id, cancelled.Id);
        Assert.Equal(OrderStatus.Cancelled, cancelled.Status);
        Assert.Null(contender.Database.CurrentTransaction);
        Assert.Empty(contender.ChangeTracker.Entries());

        await using var reader = database.CreateContext();
        var stored = await reader.Orders.AsNoTracking().ToListAsync(ct);

        Assert.Equal(OrderStatus.Cancelled, stored.Single(order => order.Id == target.Id).Status);
        Assert.Equal(OrderStatus.Created, stored.Single(order => order.Id == orders[1].Id).Status);
        // Enough capacity remains for an erroneous second release, so CHECK alone would not catch it.
        Assert.Equal(3, (await reader.DeliverySlots.SingleAsync(ct)).Reserved);
        Assert.Equal(2, await reader.Orders.CountAsync(ct));
    }

    private static async Task WaitForBlockedUpdateAsync(TestDatabase database, int blockingPid, CancellationToken ct) {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (
                SELECT 1 FROM pg_stat_activity
                WHERE datname = current_database() AND @blocker = ANY(pg_blocking_pids(pid))
                  AND query LIKE '%UPDATE orders%'
            )
            """, connection);
        command.Parameters.AddWithValue("blocker", blockingPid);

        while (!(bool)(await command.ExecuteScalarAsync(ct))!) {
            // Wait for an observed database lock within the test deadline, not an assumed request duration.
            await Task.Delay(TimeSpan.FromMilliseconds(25), ct);
        }
    }

    private static async Task<Order[]> CreateOrdersAsync(TestDatabase database, int capacity, params int[] quantities) {
        var slot = new DeliverySlot(Guid.NewGuid(), capacity, Now.AddDays(1), Now.AddDays(1).AddHours(4));
        await using var context = database.CreateContext();
        context.DeliverySlots.Add(slot);
        await context.SaveChangesAsync();

        var repository = new EfOrderRepository(context);
        var orders = quantities.Select(quantity => new Order(Guid.NewGuid(), slot.Id, quantity, Now)).ToArray();

        foreach (var order in orders) {
            Assert.Equal(CreateOrderOutcome.Created, (await repository.CreateWithReservationAsync(order)).Outcome);
        }

        return orders;
    }
}
