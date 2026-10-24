using DispatchFlow.Application.DeliverySlots;
using DispatchFlow.Application.Orders;
using DispatchFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DispatchFlow.IntegrationTests.Persistence;

[Collection("PostgreSQL")]
public sealed class OrderIdempotencyTests(PostgreSqlFixture postgres) {
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Create_WhenUniqueKeyIsUncommitted_WaitsForItsTransaction(bool commitWinner, bool differentQuantity) {
        // Arrange.
        await using var database = await postgres.CreateDatabaseAsync();
        var slot = await InsertSlotAsync(database, 2);
        var winner = new Order(Guid.NewGuid(), slot.Id, 2, Now, "contended-key");
        var candidate = new Order(Guid.NewGuid(), slot.Id, differentQuantity ? 1 : 2, Now, winner.IdempotencyKey);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;

        await using var writer = database.CreateContext();
        await using var transaction = await writer.Database.BeginTransactionAsync(ct);
        writer.Orders.Add(winner);
        await writer.SaveChangesAsync(ct);

        await writer.DeliverySlots
            .Where(item => item.Id == slot.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Reserved, 2), ct);

        int blockingPid = ((NpgsqlConnection)writer.Database.GetDbConnection()).ProcessID;
        await using var contender = database.CreateContext();

        // Act.
        var pending = new EfOrderRepository(contender).CreateWithReservationAsync(candidate, ct);

        // Observe a real PostgreSQL lock wait, so the initial SELECT cannot hide this race.
        await WaitForBlockedInsertAsync(database, blockingPid, ct);
        Assert.False(pending.IsCompleted);

        if (commitWinner) {
            await transaction.CommitAsync(ct);
        } else {
            await transaction.RollbackAsync(ct);
        }

        var result = await pending;

        // Assert.
        var expectedOutcome = !commitWinner
            ? CreateOrderOutcome.Created
            : differentQuantity
                ? CreateOrderOutcome.IdempotencyConflict
                : CreateOrderOutcome.Replayed;

        Assert.Equal(expectedOutcome, result.Outcome);

        if (expectedOutcome == CreateOrderOutcome.IdempotencyConflict) {
            Assert.Null(result.Order);
        } else {
            Assert.Equal(
                commitWinner ? winner.Id : candidate.Id,
                Assert.IsType<Order>(result.Order).Id);
        }

        Assert.Null(contender.Database.CurrentTransaction);

        if (commitWinner) {
            Assert.Empty(contender.ChangeTracker.Entries<Order>());
        }

        Assert.Equal(0, await contender.SaveChangesAsync(ct));

        await using var reader = database.CreateContext();
        var stored = Assert.Single(await reader.Orders.AsNoTracking().ToListAsync(ct));

        Assert.Equal(commitWinner ? winner.Id : candidate.Id, stored.Id);
        Assert.Equal("contended-key", stored.IdempotencyKey);
        Assert.Equal(2, (await reader.DeliverySlots.SingleAsync(ct)).Reserved);
    }

    [Fact]
    public async Task Create_WhenCapacityIsUnavailable_RollsBackOrderAndKey_AndContextCanBeReused() {
        // Arrange.
        await using var database = await postgres.CreateDatabaseAsync();
        var fullSlot = await InsertSlotAsync(database, 1);
        var availableSlot = await InsertSlotAsync(database, 1);

        await using (var setup = database.CreateContext()) {
            await new EfOrderRepository(setup).CreateWithReservationAsync(
                new Order(Guid.NewGuid(), fullSlot.Id, 1, Now));
        }

        await using var context = database.CreateContext();
        var repository = new EfOrderRepository(context);
        var rejected = new Order(Guid.NewGuid(), fullSlot.Id, 1, Now, "retry-after-capacity-error");

        // Act.
        var result = await repository.CreateWithReservationAsync(rejected);

        // Assert.
        Assert.Equal(CreateOrderOutcome.InsufficientCapacity, result.Outcome);
        Assert.Empty(context.ChangeTracker.Entries<Order>());
        Assert.Null(context.Database.CurrentTransaction);
        Assert.Equal(0, await context.SaveChangesAsync());

        // Retry with the same key using the same context.
        var retry = new Order(Guid.NewGuid(), availableSlot.Id, 1, Now, rejected.IdempotencyKey);
        Assert.Equal(CreateOrderOutcome.Created, (await repository.CreateWithReservationAsync(retry)).Outcome);

        await using var reader = database.CreateContext();
        var orders = await reader.Orders.AsNoTracking().ToListAsync();

        Assert.Equal(2, orders.Count);
        Assert.DoesNotContain(orders, order => order.Id == rejected.Id);
        Assert.Equal(
            retry.Id,
            Assert.Single(orders, order => order.IdempotencyKey == rejected.IdempotencyKey).Id);

        Assert.All(await reader.DeliverySlots.ToListAsync(), slot => Assert.Equal(1, slot.Reserved));
    }

    [Fact]
    public async Task Create_WhenPrimaryKeyIsDuplicate_DoesNotTreatItAsIdempotencyConflict() {
        // Arrange.
        await using var database = await postgres.CreateDatabaseAsync();
        var slot = await InsertSlotAsync(database, 3);
        var original = new Order(Guid.NewGuid(), slot.Id, 1, Now, "original-key");

        await using (var setup = database.CreateContext()) {
            await new EfOrderRepository(setup).CreateWithReservationAsync(original);
        }

        await using var context = database.CreateContext();
        var repository = new EfOrderRepository(context);
        var duplicate = new Order(original.Id, slot.Id, 1, Now, "unused-key");

        // Act.
        var error = await Assert.ThrowsAsync<DbUpdateException>(() =>
            repository.CreateWithReservationAsync(duplicate));

        // Assert.
        var postgresError = Assert.IsType<PostgresException>(error.InnerException);

        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgresError.SqlState);
        Assert.Equal("pk_orders", postgresError.ConstraintName);
        Assert.Empty(context.ChangeTracker.Entries<Order>());
        Assert.Null(context.Database.CurrentTransaction);

        // The unrelated unique violation must not consume the new idempotency key.
        var retry = new Order(Guid.NewGuid(), slot.Id, 1, Now, duplicate.IdempotencyKey);
        Assert.Equal(CreateOrderOutcome.Created, (await repository.CreateWithReservationAsync(retry)).Outcome);

        await using var reader = database.CreateContext();
        Assert.Equal(2, await reader.Orders.CountAsync());
        Assert.Equal(2, (await reader.DeliverySlots.SingleAsync()).Reserved);
    }

    [Fact]
    public async Task Create_WhenSlotIsDeletedDuringInsert_ReturnsNotFoundAndDoesNotKeepKey() {
        // Arrange.
        await using var database = await postgres.CreateDatabaseAsync();
        var deletedSlot = await InsertSlotAsync(database, 1);
        var availableSlot = await InsertSlotAsync(database, 1);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;

        await using var writer = database.CreateContext();
        await using var transaction = await writer.Database.BeginTransactionAsync(ct);
        await writer.DeliverySlots
            .Where(slot => slot.Id == deletedSlot.Id)
            .ExecuteDeleteAsync(ct);

        int blockingPid = ((NpgsqlConnection)writer.Database.GetDbConnection()).ProcessID;

        await using var contender = database.CreateContext();
        var repository = new EfOrderRepository(contender);
        var order = new Order(Guid.NewGuid(), deletedSlot.Id, 1, Now, "slot-deletion-race");

        // Act.
        var pending = repository.CreateWithReservationAsync(order, ct);

        // The existence check sees the old row; the FK check waits for DELETE to commit.
        await WaitForBlockedInsertAsync(database, blockingPid, ct);
        await transaction.CommitAsync(ct);

        var result = await pending;

        // Assert.
        Assert.Equal(CreateOrderOutcome.SlotNotFound, result.Outcome);
        Assert.Empty(contender.ChangeTracker.Entries<Order>());
        Assert.Null(contender.Database.CurrentTransaction);
        Assert.False(await contender.Orders.AnyAsync(ct));

        // A missing slot must not prevent reuse of the key for a valid slot.
        var retry = new Order(Guid.NewGuid(), availableSlot.Id, 1, Now, order.IdempotencyKey);
        Assert.Equal(CreateOrderOutcome.Created, (await repository.CreateWithReservationAsync(retry, ct)).Outcome);

        await using var reader = database.CreateContext();
        Assert.Equal(retry.Id, (await reader.Orders.SingleAsync(ct)).Id);
        Assert.Equal(1, (await reader.DeliverySlots.SingleAsync(ct)).Reserved);
    }

    private static async Task WaitForBlockedInsertAsync(TestDatabase database, int blockingPid, CancellationToken ct) {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (
                SELECT 1 FROM pg_stat_activity
                WHERE datname = current_database() AND @blocker = ANY(pg_blocking_pids(pid))
                  AND query LIKE '%INSERT INTO orders%'
            )
            """, connection);
        command.Parameters.AddWithValue("blocker", blockingPid);

        while (!(bool)(await command.ExecuteScalarAsync(ct))!) {
            // Poll a database condition within the test deadline, rather than guessing when INSERT starts.
            await Task.Delay(TimeSpan.FromMilliseconds(25), ct);
        }
    }

    private static async Task<DeliverySlot> InsertSlotAsync(TestDatabase database, int capacity) {
        var slot = new DeliverySlot(Guid.NewGuid(), capacity, Now.AddDays(1), Now.AddDays(1).AddHours(4));
        await using var context = database.CreateContext();
        context.DeliverySlots.Add(slot);
        await context.SaveChangesAsync();

        return slot;
    }
}
