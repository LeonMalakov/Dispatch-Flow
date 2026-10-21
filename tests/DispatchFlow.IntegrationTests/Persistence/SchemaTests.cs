using DispatchFlow.Application.DeliverySlots;
using DispatchFlow.Application.Orders;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DispatchFlow.IntegrationTests.Persistence;

[Collection("PostgreSQL")]
public sealed class SchemaTests(PostgreSqlFixture postgres) {
    private static readonly DateTimeOffset StartsAtUtc = new(2026, 10, 21, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Migrations_ApplyToEmptyDatabase_AndCanBeAppliedAgain() {
        await using var database = await postgres.CreateDatabaseAsync(applyMigrations: false);
        await using var context = database.CreateContext();
        var knownMigrations = context.Database.GetMigrations().ToArray();

        Assert.Empty(await context.Database.GetAppliedMigrationsAsync());
        await context.Database.MigrateAsync();
        Assert.Equal(knownMigrations, await context.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());

        var slot = CreateSlot();
        context.DeliverySlots.Add(slot);
        await context.SaveChangesAsync();
        await context.Database.MigrateAsync();

        Assert.Equal(1, await context.DeliverySlots.CountAsync());
        Assert.Equal(knownMigrations, await context.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
    }

    [Theory]
    [InlineData(OrderStatus.Created)]
    [InlineData(OrderStatus.Cancelled)]
    public async Task Entities_RoundTripWithUtcTimesAndIntegerStatus(OrderStatus status) {
        await using var database = await postgres.CreateDatabaseAsync();
        var slot = CreateSlot();
        var order = new Order(Guid.NewGuid(), slot.Id, 2, StartsAtUtc.AddDays(-1));
        await using (var context = database.CreateContext()) {
            context.AddRange(slot, order);
            await context.SaveChangesAsync();
        }

        await database.ExecuteAsync("UPDATE orders SET status = @status WHERE id = @id",
            new NpgsqlParameter("status", (int)status), new NpgsqlParameter("id", order.Id));

        await using var reader = database.CreateContext();
        var storedSlot = await reader.DeliverySlots.AsNoTracking().SingleAsync();
        var storedOrder = await reader.Orders.AsNoTracking().SingleAsync();
        Assert.Equal(slot.Id, storedSlot.Id);
        Assert.Equal(slot.Capacity, storedSlot.Capacity);
        Assert.Equal(0, storedSlot.Reserved);
        Assert.Equal(StartsAtUtc, storedSlot.StartsAtUtc);
        Assert.Equal(StartsAtUtc.AddHours(4), storedSlot.EndsAtUtc);
        Assert.Equal(TimeSpan.Zero, storedSlot.StartsAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, storedSlot.EndsAtUtc.Offset);
        Assert.Equal(order.Id, storedOrder.Id);
        Assert.Equal(slot.Id, storedOrder.SlotId);
        Assert.Equal(2, storedOrder.Quantity);
        Assert.Equal(order.CreatedAtUtc, storedOrder.CreatedAtUtc);
        Assert.Equal(TimeSpan.Zero, storedOrder.CreatedAtUtc.Offset);
        Assert.Equal(status, storedOrder.Status);

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT status, pg_typeof(status)::text FROM orders", connection);
        await using var rows = await command.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
        Assert.Equal((int)status, rows.GetInt32(0));
        Assert.Equal("integer", rows.GetString(1));
    }

    [Theory]
    [InlineData(0, 0, "ck_delivery_slots_capacity_positive")]
    [InlineData(-1, 0, "ck_delivery_slots_capacity_positive")]
    [InlineData(10, -1, "ck_delivery_slots_reserved_within_capacity")]
    [InlineData(10, 11, "ck_delivery_slots_reserved_within_capacity")]
    public async Task Slots_RejectInvalidCapacityAndReserved(int capacity, int reserved, string constraint) {
        await using var database = await postgres.CreateDatabaseAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(
            """
            INSERT INTO delivery_slots (id, capacity, reserved, starts_at_utc, ends_at_utc)
            VALUES (@id, @capacity, @reserved, @start, @end)
            """,
            new NpgsqlParameter("id", Guid.NewGuid()), new NpgsqlParameter("capacity", capacity),
            new NpgsqlParameter("reserved", reserved), new NpgsqlParameter("start", StartsAtUtc),
            new NpgsqlParameter("end", StartsAtUtc.AddHours(4))));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal(constraint, error.ConstraintName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Slots_RejectEndAtOrBeforeStart(int endOffsetHours) {
        await using var database = await postgres.CreateDatabaseAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(
            """
            INSERT INTO delivery_slots (id, capacity, reserved, starts_at_utc, ends_at_utc)
            VALUES (@id, 10, 0, @start, @end)
            """,
            new NpgsqlParameter("id", Guid.NewGuid()), new NpgsqlParameter("start", StartsAtUtc),
            new NpgsqlParameter("end", StartsAtUtc.AddHours(endOffsetHours))));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal("ck_delivery_slots_time_range", error.ConstraintName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Orders_RejectNonPositiveQuantity(int quantity) {
        await using var database = await postgres.CreateDatabaseAsync();
        var slot = await InsertSlotAsync(database);

        var error = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(
            """
            INSERT INTO orders (id, slot_id, quantity, status, created_at_utc)
            VALUES (@id, @slot, @quantity, 0, @created)
            """,
            new NpgsqlParameter("id", Guid.NewGuid()), new NpgsqlParameter("slot", slot.Id),
            new NpgsqlParameter("quantity", quantity), new NpgsqlParameter("created", StartsAtUtc)));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal("ck_orders_quantity_positive", error.ConstraintName);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public async Task Orders_RejectUnknownStatus(int status) {
        await using var database = await postgres.CreateDatabaseAsync();
        var slot = await InsertSlotAsync(database);

        var error = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(
            """
            INSERT INTO orders (id, slot_id, quantity, status, created_at_utc)
            VALUES (@id, @slot, 1, @status, @created)
            """,
            new NpgsqlParameter("id", Guid.NewGuid()), new NpgsqlParameter("slot", slot.Id),
            new NpgsqlParameter("status", status), new NpgsqlParameter("created", StartsAtUtc)));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal("ck_orders_status", error.ConstraintName);
    }

    [Theory]
    [InlineData("delivery_slots", "id")]
    [InlineData("delivery_slots", "capacity")]
    [InlineData("delivery_slots", "reserved")]
    [InlineData("delivery_slots", "starts_at_utc")]
    [InlineData("delivery_slots", "ends_at_utc")]
    [InlineData("orders", "id")]
    [InlineData("orders", "slot_id")]
    [InlineData("orders", "quantity")]
    [InlineData("orders", "status")]
    [InlineData("orders", "created_at_utc")]
    public async Task EntityColumns_RejectNull(string table, string column) {
        await using var database = await postgres.CreateDatabaseAsync();
        var slot = await InsertSlotAsync(database);
        var order = new Order(Guid.NewGuid(), slot.Id, 1, StartsAtUtc);
        await using (var context = database.CreateContext()) {
            context.Orders.Add(order);
            await context.SaveChangesAsync();
        }

        // Identifiers are fixed InlineData cases, not external input. Bypass entity validation.
        var error = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(
            $"UPDATE {table} SET {column} = NULL"));

        Assert.Equal(PostgresErrorCodes.NotNullViolation, error.SqlState);
        Assert.Equal(table, error.TableName);
        Assert.Equal(column, error.ColumnName);
    }

    [Fact]
    public async Task Orders_RejectMissingSlot() {
        await using var database = await postgres.CreateDatabaseAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(
            """
            INSERT INTO orders (id, slot_id, quantity, status, created_at_utc)
            VALUES (@id, @slot, 1, 0, @created)
            """,
            new NpgsqlParameter("id", Guid.NewGuid()), new NpgsqlParameter("slot", Guid.NewGuid()),
            new NpgsqlParameter("created", StartsAtUtc)));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        Assert.Equal("fk_orders_delivery_slots_slot_id", error.ConstraintName);
    }

    [Fact]
    public async Task Slots_WithOrders_CannotBeDeleted() {
        await using var database = await postgres.CreateDatabaseAsync();
        var slot = await InsertSlotAsync(database);
        await using (var context = database.CreateContext()) {
            context.Orders.Add(new Order(Guid.NewGuid(), slot.Id, 1, StartsAtUtc));
            await context.SaveChangesAsync();
        }

        var error = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(
            "DELETE FROM delivery_slots WHERE id = @id", new NpgsqlParameter("id", slot.Id)));

        Assert.Equal(PostgresErrorCodes.RestrictViolation, error.SqlState);
        Assert.Equal("fk_orders_delivery_slots_slot_id", error.ConstraintName);
        await using var reader = database.CreateContext();
        Assert.Equal(1, await reader.DeliverySlots.CountAsync());
        Assert.Equal(1, await reader.Orders.CountAsync());
    }

    [Theory]
    [InlineData("delivery_slots", "pk_delivery_slots")]
    [InlineData("orders", "pk_orders")]
    public async Task PrimaryKeys_RejectDuplicateIds(string table, string constraint) {
        await using var database = await postgres.CreateDatabaseAsync();
        var slot = await InsertSlotAsync(database);
        await using (var context = database.CreateContext()) {
            context.Orders.Add(new Order(Guid.NewGuid(), slot.Id, 1, StartsAtUtc));
            await context.SaveChangesAsync();
        }

        var error = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(
            $"INSERT INTO {table} SELECT * FROM {table}"));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        Assert.Equal(constraint, error.ConstraintName);
    }

    private static DeliverySlot CreateSlot() => new(Guid.NewGuid(), 10, StartsAtUtc, StartsAtUtc.AddHours(4));

    private static async Task<DeliverySlot> InsertSlotAsync(TestDatabase database) {
        var slot = CreateSlot();
        await using var context = database.CreateContext();
        context.DeliverySlots.Add(slot);
        await context.SaveChangesAsync();
        return slot;
    }
}