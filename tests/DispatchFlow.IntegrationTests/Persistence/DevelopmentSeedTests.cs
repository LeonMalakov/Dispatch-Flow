using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DispatchFlow.IntegrationTests.Persistence;

[Collection("PostgreSQL")]
public sealed class DevelopmentSeedTests(PostgreSqlFixture postgres) {
    [Theory]
    [InlineData("Development", false)]
    [InlineData("Development", null)]
    [InlineData("Production", true)]
    [InlineData("Staging", true)]
    public async Task Startup_DoesNotSeedUnlessExplicitlyEnabledInDevelopment(string environment, bool? enabled) {
        // Arrange.
        await using var database = await postgres.CreateDatabaseAsync();
        await using var factory = new TestApiFactory(database.ConnectionString, environment, enabled);

        // Act.
        _ = factory.Services;

        // Assert.
        await using var context = database.CreateContext();
        Assert.Empty(await context.DeliverySlots.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task DevelopmentSeed_RepeatedStartupPreservesExistingDataAndAddsOnlyMissingSlots() {
        await using var database = await postgres.CreateDatabaseAsync();
        await StartApiAsync(database, "Development", true);

        Guid slotId;
        DateTimeOffset startsAtUtc;
        DateTimeOffset endsAtUtc;

        await using (var context = database.CreateContext()) {
            var slots = await context.DeliverySlots
                .OrderBy(slot => slot.StartsAtUtc)
                .ToListAsync();

            Assert.Equal(2, slots.Count);
            Assert.All(slots, slot => {
                Assert.True(slot.Capacity > 0);
                Assert.Equal(0, slot.Reserved);
                Assert.True(slot.StartsAtUtc < slot.EndsAtUtc);
            });

            slotId = slots[0].Id;
            startsAtUtc = slots[0].StartsAtUtc.AddDays(7);
            endsAtUtc = slots[0].EndsAtUtc.AddDays(7);
        }

        await database.ExecuteAsync(
            "UPDATE delivery_slots SET capacity = 50, reserved = 7, starts_at_utc = @start, ends_at_utc = @end WHERE id = @id",
            new NpgsqlParameter("start", startsAtUtc),
            new NpgsqlParameter("end", endsAtUtc),
            new NpgsqlParameter("id", slotId));

        await StartApiAsync(database, "Development", true);

        await using (var context = database.CreateContext()) {
            Assert.Equal(2, await context.DeliverySlots.CountAsync());
            var existing = await context.DeliverySlots.SingleAsync(slot => slot.Id == slotId);

            Assert.Equal(50, existing.Capacity);
            Assert.Equal(7, existing.Reserved);
            Assert.Equal(startsAtUtc, existing.StartsAtUtc);
            Assert.Equal(endsAtUtc, existing.EndsAtUtc);
        }

        await database.ExecuteAsync(
            "DELETE FROM delivery_slots WHERE id <> @id",
            new NpgsqlParameter("id", slotId));

        await StartApiAsync(database, "Development", true);

        await using var reader = database.CreateContext();
        Assert.Equal(2, await reader.DeliverySlots.CountAsync());
        Assert.Equal(7, (await reader.DeliverySlots.SingleAsync(slot => slot.Id == slotId)).Reserved);
    }

    [Fact]
    public async Task Startup_DoesNotApplyMigrationsAutomatically() {
        // Arrange.
        await using var database = await postgres.CreateDatabaseAsync(applyMigrations: false);

        // Act.
        await StartApiAsync(database, "Development", false);

        // Assert.
        await using var context = database.CreateContext();
        Assert.Empty(await context.Database.GetAppliedMigrationsAsync());
        Assert.NotEmpty(await context.Database.GetPendingMigrationsAsync());
    }

    private static async Task StartApiAsync(TestDatabase database, string environment, bool enabled) {
        await using var factory = new TestApiFactory(database.ConnectionString, environment, enabled);

        _ = factory.Services;
    }
}
