using System.Net;
using System.Net.Http.Json;
using DispatchFlow.Api.Contracts.Orders;
using DispatchFlow.Application.DeliverySlots;
using DispatchFlow.IntegrationTests.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace DispatchFlow.IntegrationTests.Api;

[Collection("PostgreSQL")]
public sealed class OrderIdempotencyApiTests(PostgreSqlFixture postgres) {
    [Fact]
    public async Task Post_ReplaysExistingOrder_EvenWhenSlotIsFull() {
        // Arrange.
        await using var database = await postgres.CreateDatabaseAsync();
        var slot = await InsertSlotAsync(database, 2);
        await using var factory = new TestApiFactory(database.ConnectionString, "Development", false);
        using var client = CreateClient(factory);

        var key = "Az09-_.:".PadRight(128, 'a');
        using var first = await PostAsync(client, slot.Id, 2, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var created = Assert.IsType<OrderResponse>(await first.Content.ReadFromJsonAsync<OrderResponse>());

        // Act.
        using var replay = await PostAsync(client, slot.Id, 2, key);

        // Assert.
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var existing = Assert.IsType<OrderResponse>(await replay.Content.ReadFromJsonAsync<OrderResponse>());
        Assert.Equal(created with { CreatedAtUtc = existing.CreatedAtUtc }, existing);
        // PostgreSQL stores microseconds; .NET timestamps can also contain 100 ns ticks.
        Assert.InRange((created.CreatedAtUtc - existing.CreatedAtUtc).Duration().Ticks, 0L, 9L);

        await using var reader = database.CreateContext();
        var stored = Assert.Single(await reader.Orders.AsNoTracking().ToListAsync());

        Assert.Equal(created.Id, stored.Id);
        Assert.Equal(slot.Id, stored.SlotId);
        Assert.Equal(2, stored.Quantity);
        Assert.Equal(key, stored.IdempotencyKey);
        Assert.Equal(existing.CreatedAtUtc, stored.CreatedAtUtc);
        Assert.Equal(2, (await reader.DeliverySlots.SingleAsync()).Reserved);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Post_WhenKeyIsReusedWithDifferentPayload_ReturnsConflict(bool changeSlot) {
        // Arrange.
        await using var database = await postgres.CreateDatabaseAsync();
        var slot = await InsertSlotAsync(database, 10);
        var otherSlot = await InsertSlotAsync(database, 10);
        await using var factory = new TestApiFactory(database.ConnectionString, "Development", false);
        using var client = CreateClient(factory);

        using var first = await PostAsync(client, slot.Id, 2, "payload-key");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // Act.
        using var conflict = await PostAsync(
            client,
            changeSlot ? otherSlot.Id : slot.Id,
            changeSlot ? 2 : 3,
            "payload-key");

        // Assert.
        await AssertProblemAsync(conflict, HttpStatusCode.Conflict, "Idempotency key conflict.");

        await using var reader = database.CreateContext();
        Assert.Equal(1, await reader.Orders.CountAsync());
        Assert.Equal(2, (await reader.DeliverySlots.SingleAsync(item => item.Id == slot.Id)).Reserved);
        Assert.Equal(0, (await reader.DeliverySlots.SingleAsync(item => item.Id == otherSlot.Id)).Reserved);
    }

    [Theory]
    [InlineData("first-key", "second-key")]
    [InlineData("Case-Key", "case-key")]
    public async Task Post_WithDifferentKeys_CreatesIndependentOrders(string firstKey, string secondKey) {
        // Arrange.
        await using var database = await postgres.CreateDatabaseAsync();
        var slot = await InsertSlotAsync(database, 2);
        await using var factory = new TestApiFactory(database.ConnectionString, "Development", false);
        using var client = CreateClient(factory);

        // Act.
        using var first = await PostAsync(client, slot.Id, 1, firstKey);
        using var second = await PostAsync(client, slot.Id, 1, secondKey);

        // Assert.
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        var firstOrder = Assert.IsType<OrderResponse>(await first.Content.ReadFromJsonAsync<OrderResponse>());
        var secondOrder = Assert.IsType<OrderResponse>(await second.Content.ReadFromJsonAsync<OrderResponse>());

        Assert.NotEqual(firstOrder.Id, secondOrder.Id);

        await using var reader = database.CreateContext();
        var orders = await reader.Orders.AsNoTracking().ToListAsync();

        Assert.Equal(2, orders.Count);
        Assert.Equal(firstKey, orders.Single(order => order.Id == firstOrder.Id).IdempotencyKey);
        Assert.Equal(secondKey, orders.Single(order => order.Id == secondOrder.Id).IdempotencyKey);
        Assert.Equal(2, (await reader.DeliverySlots.SingleAsync()).Reserved);
    }

    [Fact]
    public async Task Post_WhenKeyIsInvalid_Returns400WithoutChangingData() {
        // Arrange.
        await using var database = await postgres.CreateDatabaseAsync();
        var slot = await InsertSlotAsync(database, 2);
        await using var factory = new TestApiFactory(database.ConnectionString, "Development", false);
        using var client = CreateClient(factory);

        string[] invalidKeys = [" ", " key", "key ", "key value", "key,value", "key/value", "ключ", new string('a', 129)];

        // Act & Assert.
        foreach (var key in invalidKeys) {
            using var response = await PostAsync(client, slot.Id, 1, key);
            await AssertInvalidHeaderAsync(response);
        }

        await using var reader = database.CreateContext();
        Assert.Equal(0, await reader.Orders.CountAsync());
        Assert.Equal(0, (await reader.DeliverySlots.SingleAsync()).Reserved);
    }

    [Fact]
    public async Task Post_WhenHeaderIsPresentButEmpty_Returns400() {
        // Arrange.
        await using var database = await postgres.CreateDatabaseAsync();
        var slot = await InsertSlotAsync(database, 2);
        await using var factory = new TestApiFactory(database.ConnectionString, "Development", false);

        // TestHost drops empty header values when copying HttpRequestMessage; preserve this wire-level case explicitly.
        using var handler = factory.Server.CreateHandler(context =>
            context.Request.Headers["Idempotency-Key"] = string.Empty);
        using var client = new HttpClient(handler) {
            BaseAddress = new Uri("https://localhost")
        };

        // Act.
        using var response = await PostAsync(client, slot.Id, 1, null);

        // Assert.
        await AssertInvalidHeaderAsync(response);

        await using var reader = database.CreateContext();
        Assert.Equal(0, await reader.Orders.CountAsync());
        Assert.Equal(0, (await reader.DeliverySlots.SingleAsync()).Reserved);
    }

    [Fact]
    public async Task Post_WhenHeaderHasMultipleValues_Returns400() {
        // Arrange.
        await using var database = await postgres.CreateDatabaseAsync();
        var slot = await InsertSlotAsync(database, 2);
        await using var factory = new TestApiFactory(database.ConnectionString, "Development", false);
        using var client = CreateClient(factory);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders") {
            Content = JsonContent.Create(new { slotId = slot.Id, quantity = 1 })
        };
        Assert.True(request.Headers.TryAddWithoutValidation("Idempotency-Key", new[] { "first", "second" }));

        // Act.
        using var response = await client.SendAsync(request);

        // Assert.
        await AssertInvalidHeaderAsync(response);

        await using var reader = database.CreateContext();
        Assert.Equal(0, await reader.Orders.CountAsync());
        Assert.Equal(0, (await reader.DeliverySlots.SingleAsync()).Reserved);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Post_WhenSlotIsMissingOrFull_DoesNotKeepKey(bool missingSlot) {
        // Arrange.
        await using var database = await postgres.CreateDatabaseAsync();
        var available = await InsertSlotAsync(database, 1);
        var full = await InsertSlotAsync(database, 1);
        await using var factory = new TestApiFactory(database.ConnectionString, "Development", false);
        using var client = CreateClient(factory);

        if (!missingSlot) {
            using var initial = await PostAsync(client, full.Id, 1, null);
            Assert.Equal(HttpStatusCode.Created, initial.StatusCode);
        }

        // Act.
        using var failed = await PostAsync(client, missingSlot ? Guid.NewGuid() : full.Id, 1, "retry-key");

        // Assert.
        await AssertProblemAsync(
            failed,
            missingSlot ? HttpStatusCode.NotFound : HttpStatusCode.Conflict,
            missingSlot ? "Delivery slot not found." : "Insufficient delivery slot capacity.");

        await using (var reader = database.CreateContext()) {
            Assert.Equal(missingSlot ? 0 : 1, await reader.Orders.CountAsync());
            Assert.False(await reader.Orders.AnyAsync(order => order.IdempotencyKey == "retry-key"));
            Assert.Equal(missingSlot ? 0 : 1, await reader.DeliverySlots.SumAsync(slot => slot.Reserved));
        }

        // An unsuccessful request must leave the key available for a valid request.
        using var retry = await PostAsync(client, available.Id, 1, "retry-key");
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
    }

    [Fact]
    public async Task Post_WhenReservationThrows_RollsBackOrderAndKey_AndReturnsSafe500() {
        // Arrange.
        await using var database = await postgres.CreateDatabaseAsync();
        var slot = await InsertSlotAsync(database, 2);

        // This trigger exists only in this test database and fails after the keyed INSERT.
        await database.ExecuteAsync("""
            CREATE FUNCTION fail_reservation() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'test reservation failure';
            END;
            $$;
            CREATE TRIGGER fail_reservation BEFORE UPDATE OF reserved ON delivery_slots
            FOR EACH ROW EXECUTE FUNCTION fail_reservation();
            """);

        await using var factory = new TestApiFactory(database.ConnectionString, "Development", false);
        using var client = CreateClient(factory);

        // Act.
        using var failed = await PostAsync(client, slot.Id, 1, "retry-after-db-error");

        // Assert.
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal("application/problem+json", failed.Content.Headers.ContentType?.MediaType);
        var body = await failed.Content.ReadAsStringAsync();

        Assert.DoesNotContain("test reservation failure", body);
        Assert.DoesNotContain("fail_reservation", body);
        Assert.DoesNotContain("PostgresException", body);

        await using (var reader = database.CreateContext()) {
            Assert.Equal(0, await reader.Orders.CountAsync());
            Assert.Equal(0, (await reader.DeliverySlots.SingleAsync()).Reserved);
        }

        // Remove the test failure and retry the same key.
        await database.ExecuteAsync("DROP TRIGGER fail_reservation ON delivery_slots; DROP FUNCTION fail_reservation();");

        using var retry = await PostAsync(client, slot.Id, 1, "retry-after-db-error");
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);

        await using var finalReader = database.CreateContext();
        Assert.Equal("retry-after-db-error", (await finalReader.Orders.SingleAsync()).IdempotencyKey);
        Assert.Equal(1, (await finalReader.DeliverySlots.SingleAsync()).Reserved);
    }

    [Theory]
    [InlineData("same")]
    [InlineData("quantity")]
    [InlineData("slot")]
    public async Task Post_ConcurrentRequestsWithOneKey_CreateOnlyOneOrder(string difference) {
        // Arrange.
        await using var database = await postgres.CreateDatabaseAsync();
        var slot = await InsertSlotAsync(database, 2);
        var otherSlot = await InsertSlotAsync(database, 2);
        await using var factory = new TestApiFactory(database.ConnectionString, "Development", false);
        using var client = CreateClient(factory);

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = Enumerable.Range(0, 12).Select(SendAfterStartAsync).ToArray();

        // Act.
        start.SetResult();
        var results = await Task.WhenAll(pending);

        // Assert.
        var winner = Assert.IsType<OrderResponse>(
            Assert.Single(results, result => result.Status == HttpStatusCode.Created).Order);

        Assert.Equal(
            difference == "same" ? 11 : 5,
            results.Count(result => result.Status == HttpStatusCode.OK));
        Assert.Equal(
            difference == "same" ? 0 : 6,
            results.Count(result => result.Status == HttpStatusCode.Conflict));

        foreach (var result in results) {
            if (result.Status == HttpStatusCode.Conflict) {
                Assert.True(result.SlotId != winner.SlotId || result.Quantity != winner.Quantity);
            } else {
                var order = Assert.IsType<OrderResponse>(result.Order);

                Assert.Equal(winner.Id, order.Id);
                Assert.Equal(result.SlotId, order.SlotId);
                Assert.Equal(result.Quantity, order.Quantity);
            }
        }

        await using var reader = database.CreateContext();
        var stored = Assert.Single(await reader.Orders.AsNoTracking().ToListAsync());

        Assert.Equal(winner.Id, stored.Id);
        Assert.Equal("concurrent-key", stored.IdempotencyKey);
        Assert.Equal(winner.SlotId, stored.SlotId);
        Assert.Equal(winner.Quantity, stored.Quantity);

        var slots = await reader.DeliverySlots.AsNoTracking().ToListAsync();
        Assert.All(slots, item => Assert.Equal(item.Id == winner.SlotId ? winner.Quantity : 0, item.Reserved));
        Assert.Equal(stored.Quantity, slots.Sum(item => item.Reserved));

        async Task<(HttpStatusCode Status, OrderResponse? Order, Guid SlotId, int Quantity)> SendAfterStartAsync(int index) {
            var requestedSlot = difference == "slot" && index % 2 == 1 ? otherSlot.Id : slot.Id;
            int quantity = difference == "quantity" && index % 2 == 1 ? 1 : 2;

            await start.Task;
            using var response = await PostAsync(client, requestedSlot, quantity, "concurrent-key");

            if (response.StatusCode == HttpStatusCode.Conflict) {
                await AssertProblemAsync(response, HttpStatusCode.Conflict, "Idempotency key conflict.");
                return (response.StatusCode, null, requestedSlot, quantity);
            }

            Assert.True(
                response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK,
                $"Unexpected HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

            var order = await response.Content.ReadFromJsonAsync<OrderResponse>();

            return (response.StatusCode, order, requestedSlot, quantity);
        }
    }

    private static HttpClient CreateClient(TestApiFactory factory) => factory.CreateClient(new WebApplicationFactoryClientOptions {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false
    });

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, Guid slotId, int quantity, string? key) {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders") {
            Content = JsonContent.Create(new { slotId, quantity })
        };

        if (key is not null) {
            Assert.True(request.Headers.TryAddWithoutValidation("Idempotency-Key", key));
        }

        return await client.SendAsync(request);
    }

    private static async Task AssertInvalidHeaderAsync(HttpResponseMessage response) {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = Assert.IsType<ValidationProblemDetails>(
            await response.Content.ReadFromJsonAsync<ValidationProblemDetails>());

        Assert.Equal(400, problem.Status);
        Assert.Contains("Idempotency-Key", problem.Errors.Keys);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string title) {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = Assert.IsType<ProblemDetails>(await response.Content.ReadFromJsonAsync<ProblemDetails>());

        Assert.Equal((int)status, problem.Status);
        Assert.Equal(title, problem.Title);
    }

    private static async Task<DeliverySlot> InsertSlotAsync(TestDatabase database, int capacity) {
        var startsAtUtc = new DateTimeOffset(2026, 10, 25, 8, 0, 0, TimeSpan.Zero);
        var slot = new DeliverySlot(Guid.NewGuid(), capacity, startsAtUtc, startsAtUtc.AddHours(4));
        await using var context = database.CreateContext();
        context.DeliverySlots.Add(slot);
        await context.SaveChangesAsync();

        return slot;
    }
}
