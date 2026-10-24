using System.Net;
using System.Net.Http.Json;
using DispatchFlow.Api.Contracts.Orders;
using DispatchFlow.Application.DeliverySlots;
using DispatchFlow.Application.Orders;
using DispatchFlow.IntegrationTests.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace DispatchFlow.IntegrationTests.Api;

[Collection("PostgreSQL")]
public sealed class OrderCancellationApiTests(PostgreSqlFixture postgres) {
    [Fact]
    public async Task Cancel_ReleasesOnlyTargetReservation_AndRepeatedCancellationDoesNotReleaseAgain() {
        // Arrange.
        await using var database = await postgres.CreateDatabaseAsync();
        var slot = await InsertSlotAsync(database, 5);
        var otherSlot = await InsertSlotAsync(database, 10);
        await using var factory = new TestApiFactory(database.ConnectionString, "Development", false);
        using var client = CreateClient(factory);
        var target = await CreateOrderAsync(client, slot.Id, 2);
        var survivor = await CreateOrderAsync(client, slot.Id, 3);
        var other = await CreateOrderAsync(client, otherSlot.Id, 4);

        // Act.
        using var response = await client.PostAsync($"/api/orders/{target.Id}/cancel", null);

        // Assert.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cancelled = Assert.IsType<OrderResponse>(await response.Content.ReadFromJsonAsync<OrderResponse>());
        Assert.Equal(target with { Status = "Cancelled", CreatedAtUtc = cancelled.CreatedAtUtc }, cancelled);
        // PostgreSQL stores microseconds; .NET timestamps can also contain 100 ns ticks.
        Assert.InRange((target.CreatedAtUtc - cancelled.CreatedAtUtc).Duration().Ticks, 0L, 9L);

        using var repeated = await client.PostAsync($"/api/orders/{target.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(cancelled, await repeated.Content.ReadFromJsonAsync<OrderResponse>());

        using var get = await client.GetAsync($"/api/orders/{target.Id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(cancelled, await get.Content.ReadFromJsonAsync<OrderResponse>());

        await using (var reader = database.CreateContext()) {
            var orders = await reader.Orders.AsNoTracking().ToListAsync();
            Assert.Equal(3, orders.Count);
            Assert.Equal(OrderStatus.Cancelled, orders.Single(order => order.Id == target.Id).Status);
            Assert.Equal(OrderStatus.Created, orders.Single(order => order.Id == survivor.Id).Status);
            Assert.Equal(OrderStatus.Created, orders.Single(order => order.Id == other.Id).Status);
            Assert.Equal(3, (await reader.DeliverySlots.SingleAsync(item => item.Id == slot.Id)).Reserved);
            Assert.Equal(4, (await reader.DeliverySlots.SingleAsync(item => item.Id == otherSlot.Id)).Reserved);
        }

        // Released capacity can be reserved by a new order.
        var replacement = await CreateOrderAsync(client, slot.Id, 2);
        Assert.NotEqual(target.Id, replacement.Id);
        await using var finalReader = database.CreateContext();
        Assert.Equal(4, await finalReader.Orders.CountAsync());
        Assert.Equal(5, (await finalReader.DeliverySlots.SingleAsync(item => item.Id == slot.Id)).Reserved);
        Assert.Equal(4, (await finalReader.DeliverySlots.SingleAsync(item => item.Id == otherSlot.Id)).Reserved);
    }

    [Fact]
    public async Task Cancel_WhenOrderIsMissing_Returns404WithoutChangingReservations() {
        // Arrange.
        await using var database = await postgres.CreateDatabaseAsync();
        await InsertSlotAsync(database, 5);
        await using var factory = new TestApiFactory(database.ConnectionString, "Development", false);
        using var client = CreateClient(factory);

        // Act.
        using var response = await client.PostAsync($"/api/orders/{Guid.NewGuid()}/cancel", null);

        // Assert.
        await AssertProblemAsync(response, HttpStatusCode.NotFound, "Order not found.");
        await using var reader = database.CreateContext();
        Assert.Equal(0, await reader.Orders.CountAsync());
        Assert.Equal(0, (await reader.DeliverySlots.SingleAsync()).Reserved);
    }

    [Theory]
    [InlineData("invalid-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Cancel_WhenIdIsInvalid_Returns400WithoutChangingData(string id) {
        // Arrange.
        await using var database = await postgres.CreateDatabaseAsync();
        await InsertSlotAsync(database, 5);
        await using var factory = new TestApiFactory(database.ConnectionString, "Development", false);
        using var client = CreateClient(factory);

        // Act.
        using var response = await client.PostAsync($"/api/orders/{id}/cancel", null);

        // Assert.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = Assert.IsType<ValidationProblemDetails>(
            await response.Content.ReadFromJsonAsync<ValidationProblemDetails>());
        Assert.Equal(400, problem.Status);
        Assert.NotEmpty(problem.Errors);

        await using var reader = database.CreateContext();
        Assert.Equal(0, await reader.Orders.CountAsync());
        Assert.Equal(0, (await reader.DeliverySlots.SingleAsync()).Reserved);
    }

    [Fact]
    public async Task Cancel_ConcurrentRequestsForSameOrder_ReleaseCapacityOnce() {
        // Arrange.
        await using var database = await postgres.CreateDatabaseAsync();
        var slot = await InsertSlotAsync(database, 10);
        await using var factory = new TestApiFactory(database.ConnectionString, "Development", false);
        using var client = CreateClient(factory);
        var target = await CreateOrderAsync(client, slot.Id, 2);
        var survivor = await CreateOrderAsync(client, slot.Id, 3);

        // Act.
        var responses = await CancelConcurrentlyAsync(client, Enumerable.Repeat(target.Id, 16));

        // Assert.
        Assert.All(responses, order => {
            Assert.Equal(target.Id, order.Id);
            Assert.Equal("Cancelled", order.Status);
            Assert.Equal(responses[0], order);
        });

        await using var reader = database.CreateContext();
        var orders = await reader.Orders.AsNoTracking().ToListAsync();
        Assert.Equal(2, orders.Count);
        Assert.Equal(OrderStatus.Cancelled, orders.Single(order => order.Id == target.Id).Status);
        Assert.Equal(OrderStatus.Created, orders.Single(order => order.Id == survivor.Id).Status);
        // A second release could still satisfy Reserved >= Quantity, so verify the exact balance.
        Assert.Equal(3, (await reader.DeliverySlots.SingleAsync()).Reserved);
    }

    [Fact]
    public async Task Cancel_ConcurrentRequestsForDifferentOrders_ReleaseAllReservations() {
        // Arrange.
        await using var database = await postgres.CreateDatabaseAsync();
        var slot = await InsertSlotAsync(database, 21);
        await using var factory = new TestApiFactory(database.ConnectionString, "Development", false);
        using var client = CreateClient(factory);
        var orders = new List<OrderResponse>();
        foreach (int quantity in Enumerable.Range(1, 6)) {
            orders.Add(await CreateOrderAsync(client, slot.Id, quantity));
        }

        // Act.
        var responses = await CancelConcurrentlyAsync(client, orders.Select(order => order.Id));

        // Assert.
        Assert.Equal(orders.Select(order => order.Id).Order(), responses.Select(order => order.Id).Order());
        Assert.All(responses, order => Assert.Equal("Cancelled", order.Status));

        await using var reader = database.CreateContext();
        var stored = await reader.Orders.AsNoTracking().ToListAsync();
        Assert.Equal(6, stored.Count);
        Assert.All(stored, order => Assert.Equal(OrderStatus.Cancelled, order.Status));
        Assert.Equal(0, (await reader.DeliverySlots.SingleAsync()).Reserved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel_ConcurrentCreation_PreservesReservationBalance(bool useIdempotencyKey) {
        // Arrange.
        await using var database = await postgres.CreateDatabaseAsync();
        var slot = await InsertSlotAsync(database, 5);
        await using var factory = new TestApiFactory(database.ConnectionString, "Development", false);
        using var client = CreateClient(factory);
        var original = await CreateOrderAsync(client, slot.Id, 5);
        string? key = useIdempotencyKey ? "replacement-key" : null;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation = CancelAfterStartAsync();
        var creation = CreateAfterStartAsync();

        // Act.
        start.SetResult();
        using var cancelled = await cancellation;
        using var created = await creation;

        // Assert.
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Equal("Cancelled", (await cancelled.Content.ReadFromJsonAsync<OrderResponse>())?.Status);
        Assert.True(created.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict);

        if (created.StatusCode == HttpStatusCode.Conflict) {
            await AssertProblemAsync(created, HttpStatusCode.Conflict, "Insufficient delivery slot capacity.");
        }

        await using (var reader = database.CreateContext()) {
            var orders = await reader.Orders.AsNoTracking().ToListAsync();
            Assert.Equal(OrderStatus.Cancelled, orders.Single(order => order.Id == original.Id).Status);
            Assert.Equal(created.StatusCode == HttpStatusCode.Created ? 2 : 1, orders.Count);
            int reserved = (await reader.DeliverySlots.SingleAsync()).Reserved;
            Assert.Equal(created.StatusCode == HttpStatusCode.Created ? 5 : 0, reserved);
            Assert.Equal(orders.Where(order => order.Status == OrderStatus.Created).Sum(order => order.Quantity), reserved);
        }

        if (created.StatusCode == HttpStatusCode.Conflict) {
            // A rejected creation must leave its key reusable after cancellation commits.
            await CreateOrderAsync(client, slot.Id, 5, key);
        }

        await using var finalReader = database.CreateContext();
        Assert.Equal(2, await finalReader.Orders.CountAsync());
        Assert.Equal(1, await finalReader.Orders.CountAsync(order => order.Status == OrderStatus.Created));
        Assert.Equal(5, (await finalReader.DeliverySlots.SingleAsync()).Reserved);

        async Task<HttpResponseMessage> CancelAfterStartAsync() {
            await start.Task;
            return await client.PostAsync($"/api/orders/{original.Id}/cancel", null);
        }

        async Task<HttpResponseMessage> CreateAfterStartAsync() {
            await start.Task;
            return await PostOrderAsync(client, slot.Id, 5, key);
        }
    }

    [Fact]
    public async Task Post_AfterCancellation_ReplaysCancelledOrderWithoutReservingAgain() {
        // Arrange.
        await using var database = await postgres.CreateDatabaseAsync();
        var slot = await InsertSlotAsync(database, 2);
        await using var factory = new TestApiFactory(database.ConnectionString, "Development", false);
        using var client = CreateClient(factory);
        var original = await CreateOrderAsync(client, slot.Id, 2, "original-key");
        using var cancelled = await client.PostAsync($"/api/orders/{original.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        var expected = Assert.IsType<OrderResponse>(await cancelled.Content.ReadFromJsonAsync<OrderResponse>());
        Assert.Equal("Cancelled", expected.Status);

        // Act.
        using var replay = await PostOrderAsync(client, slot.Id, 2, "original-key");
        using var conflict = await PostOrderAsync(client, slot.Id, 1, "original-key");

        // Assert.
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(expected, await replay.Content.ReadFromJsonAsync<OrderResponse>());
        await AssertProblemAsync(conflict, HttpStatusCode.Conflict, "Idempotency key conflict.");

        await using var reader = database.CreateContext();
        var stored = Assert.Single(await reader.Orders.AsNoTracking().ToListAsync());
        Assert.Equal(original.Id, stored.Id);
        Assert.Equal(OrderStatus.Cancelled, stored.Status);
        Assert.Equal("original-key", stored.IdempotencyKey);
        Assert.Equal(0, (await reader.DeliverySlots.SingleAsync()).Reserved);
    }

    [Fact]
    public async Task Cancel_WhenReleaseThrows_RollsBackStatus_AndReturnsSafe500() {
        // Arrange.
        await using var database = await postgres.CreateDatabaseAsync();
        var slot = await InsertSlotAsync(database, 2);
        await using var factory = new TestApiFactory(database.ConnectionString, "Development", false);
        using var client = CreateClient(factory);
        var original = await CreateOrderAsync(client, slot.Id, 2);

        // The test-only trigger fails after the order status UPDATE and before capacity is released.
        await database.ExecuteAsync("""
            CREATE FUNCTION fail_release() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'test cancellation failure';
            END;
            $$;
            CREATE TRIGGER fail_release BEFORE UPDATE OF reserved ON delivery_slots
            FOR EACH ROW EXECUTE FUNCTION fail_release();
            """);

        // Act.
        using var failed = await client.PostAsync($"/api/orders/{original.Id}/cancel", null);

        // Assert.
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal("application/problem+json", failed.Content.Headers.ContentType?.MediaType);
        var problem = Assert.IsType<ProblemDetails>(await failed.Content.ReadFromJsonAsync<ProblemDetails>());
        Assert.Equal(500, problem.Status);
        var body = await failed.Content.ReadAsStringAsync();
        Assert.DoesNotContain("test cancellation failure", body);
        Assert.DoesNotContain("fail_release", body);
        Assert.DoesNotContain("PostgresException", body);

        await using (var reader = database.CreateContext()) {
            Assert.Equal(OrderStatus.Created, (await reader.Orders.SingleAsync()).Status);
            Assert.Equal(2, (await reader.DeliverySlots.SingleAsync()).Reserved);
        }

        // After removing the database failure, the same cancellation can succeed.
        await database.ExecuteAsync("DROP TRIGGER fail_release ON delivery_slots; DROP FUNCTION fail_release();");
        using var retry = await client.PostAsync($"/api/orders/{original.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal("Cancelled", (await retry.Content.ReadFromJsonAsync<OrderResponse>())?.Status);

        await using var finalReader = database.CreateContext();
        Assert.Equal(OrderStatus.Cancelled, (await finalReader.Orders.SingleAsync()).Status);
        Assert.Equal(0, (await finalReader.DeliverySlots.SingleAsync()).Reserved);
    }

    private static HttpClient CreateClient(TestApiFactory factory) => factory.CreateClient(new WebApplicationFactoryClientOptions {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false
    });

    private static async Task<OrderResponse> CreateOrderAsync(HttpClient client, Guid slotId, int quantity, string? key = null) {
        using var response = await PostOrderAsync(client, slotId, quantity, key);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<OrderResponse>(await response.Content.ReadFromJsonAsync<OrderResponse>());
    }

    private static async Task<HttpResponseMessage> PostOrderAsync(HttpClient client, Guid slotId, int quantity, string? key) {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders") {
            Content = JsonContent.Create(new { slotId, quantity })
        };
        if (key is not null) {
            Assert.True(request.Headers.TryAddWithoutValidation("Idempotency-Key", key));
        }
        return await client.SendAsync(request);
    }

    private static async Task<OrderResponse[]> CancelConcurrentlyAsync(HttpClient client, IEnumerable<Guid> ids) {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = ids.Select(SendAfterStartAsync).ToArray();
        start.SetResult();
        return await Task.WhenAll(pending);

        async Task<OrderResponse> SendAfterStartAsync(Guid id) {
            await start.Task;
            using var response = await client.PostAsync($"/api/orders/{id}/cancel", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return Assert.IsType<OrderResponse>(await response.Content.ReadFromJsonAsync<OrderResponse>());
        }
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
