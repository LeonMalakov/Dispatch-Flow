using DispatchFlow.Application.DeliverySlots;
using DispatchFlow.Application.Orders;
using DispatchFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace DispatchFlow.IntegrationTests.Persistence;

[Collection("PostgreSQL")]
public sealed class IdempotencySchemaTests(PostgreSqlFixture postgres) {
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Migration_PreservesExistingOrdersAndAllowsNullKeys() {
        await using var database = await postgres.CreateDatabaseAsync(applyMigrations: false);
        await using var context = database.CreateContext();
        var migrations = context.Database.GetMigrations().ToArray();
        var keyMigration = Assert.Single(migrations, migration => migration.EndsWith("_AddOrderIdempotencyKey", StringComparison.Ordinal));
        var previousMigration = migrations.TakeWhile(migration => migration != keyMigration).Last();
        await context.GetService<IMigrator>().MigrateAsync(previousMigration);
        var slot = new DeliverySlot(Guid.NewGuid(), 10, Now.AddDays(1), Now.AddDays(1).AddHours(4));
        context.DeliverySlots.Add(slot);
        await context.SaveChangesAsync();
        var originalIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        // The pre-migration table has no key column, so use its actual SQL schema.
        await database.ExecuteAsync("""
            INSERT INTO orders (id, slot_id, quantity, status, created_at_utc)
            VALUES (@first, @slot, 1, 0, @created), (@second, @slot, 2, 0, @created);
            UPDATE delivery_slots SET reserved = 3 WHERE id = @slot;
            """, new NpgsqlParameter("first", originalIds[0]), new NpgsqlParameter("second", originalIds[1]),
            new NpgsqlParameter("slot", slot.Id), new NpgsqlParameter("created", Now));

        await context.Database.MigrateAsync();
        await context.Database.MigrateAsync();

        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        var existing = await context.Orders.AsNoTracking().ToListAsync();
        Assert.Equal(originalIds.OrderBy(id => id).ToArray(), existing.Select(order => order.Id).OrderBy(id => id).ToArray());
        Assert.All(existing, order => {
            Assert.Null(order.IdempotencyKey);
            Assert.Equal(slot.Id, order.SlotId);
            Assert.Equal(Now, order.CreatedAtUtc);
            Assert.Equal(OrderStatus.Created, order.Status);
        });
        Assert.Equal(1, existing.Single(order => order.Id == originalIds[0]).Quantity);
        Assert.Equal(2, existing.Single(order => order.Id == originalIds[1]).Quantity);
        Assert.Equal(3, (await context.DeliverySlots.AsNoTracking().SingleAsync()).Reserved);
        var keyed = new Order(Guid.NewGuid(), slot.Id, 1, Now, "after-upgrade");
        Assert.Equal(CreateOrderOutcome.Created, (await new EfOrderRepository(context).CreateWithReservationAsync(keyed)).Outcome);
        Assert.Equal(3, await context.Orders.CountAsync());
    }

    [Fact]
    public async Task Key_IsUniqueAndCaseSensitive_WhileNullKeysCanRepeat() {
        await using var database = await postgres.CreateDatabaseAsync();
        var slot = await InsertSlotAsync(database);
        await InsertOrderAsync(database, slot.Id, "Case-Key");
        await InsertOrderAsync(database, slot.Id, "case-key");
        await InsertOrderAsync(database, slot.Id, null);
        await InsertOrderAsync(database, slot.Id, null);

        var error = await Assert.ThrowsAsync<PostgresException>(() => InsertOrderAsync(database, slot.Id, "Case-Key"));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        Assert.Equal("ux_orders_idempotency_key", error.ConstraintName);
        await using var reader = database.CreateContext();
        Assert.Equal(4, await reader.Orders.CountAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("key value")]
    [InlineData(" key")]
    [InlineData("key ")]
    [InlineData("key\n")]
    [InlineData("ключ")]
    [InlineData("key,value")]
    public async Task Key_CheckConstraintRejectsInvalidFormat(string key) {
        await using var database = await postgres.CreateDatabaseAsync();
        var slot = await InsertSlotAsync(database);

        var error = await Assert.ThrowsAsync<PostgresException>(() => InsertOrderAsync(database, slot.Id, key));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal("ck_orders_idempotency_key", error.ConstraintName);
    }

    [Fact]
    public async Task Key_ColumnEnforcesMaximumLength_AndAcceptsBoundary() {
        await using var database = await postgres.CreateDatabaseAsync();
        var slot = await InsertSlotAsync(database);
        await InsertOrderAsync(database, slot.Id, new string('a', 128));

        var error = await Assert.ThrowsAsync<PostgresException>(() => InsertOrderAsync(database, slot.Id, new string('a', 129)));

        Assert.Equal(PostgresErrorCodes.StringDataRightTruncation, error.SqlState);
    }

    private static async Task<DeliverySlot> InsertSlotAsync(TestDatabase database) {
        var slot = new DeliverySlot(Guid.NewGuid(), 10, Now.AddDays(1), Now.AddDays(1).AddHours(4));
        await using var context = database.CreateContext();
        context.DeliverySlots.Add(slot);
        await context.SaveChangesAsync();
        return slot;
    }

    private static Task<int> InsertOrderAsync(TestDatabase database, Guid slotId, string? key) => database.ExecuteAsync("""
        INSERT INTO orders (id, slot_id, quantity, status, created_at_utc, idempotency_key)
        VALUES (@id, @slot, 1, 0, @created, @key)
        """, new NpgsqlParameter("id", Guid.NewGuid()), new NpgsqlParameter("slot", slotId),
        new NpgsqlParameter("created", Now), new NpgsqlParameter("key", NpgsqlTypes.NpgsqlDbType.Varchar) { Value = (object?)key ?? DBNull.Value });
}
