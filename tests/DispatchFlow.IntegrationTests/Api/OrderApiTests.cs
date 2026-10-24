using System.Net;
using System.Net.Http.Json;
using System.Text;
using DispatchFlow.Api.Contracts.Orders;
using DispatchFlow.Application.DeliverySlots;
using DispatchFlow.IntegrationTests.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace DispatchFlow.IntegrationTests.Api;

[Collection("PostgreSQL")]
public sealed class OrderApiTests(PostgreSqlFixture postgres) {
    [Fact]
    public async Task Post_CreatesOrderAndReservation_WithLocationPointingToGet() {
        await using var database = await postgres.CreateDatabaseAsync();
        var slot = await InsertSlotAsync(database, 10);
        await using var factory = new TestApiFactory(database.ConnectionString, "Development", false);
        using var client = CreateClient(factory);
        var before = DateTimeOffset.UtcNow;

        using var response = await client.PostAsJsonAsync("/api/orders", new { slotId = slot.Id, quantity = 3 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = Assert.IsType<OrderResponse>(await response.Content.ReadFromJsonAsync<OrderResponse>());

        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal(slot.Id, created.SlotId);
        Assert.Equal(3, created.Quantity);
        Assert.Equal("Created", created.Status);
        Assert.Equal(TimeSpan.Zero, created.CreatedAtUtc.Offset);
        Assert.InRange(created.CreatedAtUtc, before, DateTimeOffset.UtcNow);

        var location = Assert.IsType<Uri>(response.Headers.Location);

        Assert.Equal(
            $"/api/orders/{created.Id}",
            location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString);

        using var getResponse = await client.GetAsync(location);

        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var retrieved = Assert.IsType<OrderResponse>(await getResponse.Content.ReadFromJsonAsync<OrderResponse>());

        Assert.Equal(created.Id, retrieved.Id);
        Assert.Equal(created.SlotId, retrieved.SlotId);
        Assert.Equal(created.Quantity, retrieved.Quantity);
        Assert.Equal(created.Status, retrieved.Status);

        // PostgreSQL stores timestamps with microsecond precision; .NET supports 100 ns ticks.
        Assert.InRange((created.CreatedAtUtc - retrieved.CreatedAtUtc).Duration().Ticks, 0L, 9L);

        await using var reader = database.CreateContext();
        var stored = Assert.Single(await reader.Orders.AsNoTracking().ToListAsync());

        Assert.Equal(created.Id, stored.Id);
        Assert.Equal(slot.Id, stored.SlotId);
        Assert.Equal(3, stored.Quantity);
        Assert.Equal(retrieved.CreatedAtUtc, stored.CreatedAtUtc);
        Assert.Equal(3, (await reader.DeliverySlots.SingleAsync()).Reserved);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("{\"quantity\":1}")]
    [InlineData("{\"slotId\":\"$slotId\"}")]
    [InlineData("{\"slotId\":null,\"quantity\":1}")]
    [InlineData("{\"slotId\":\"$slotId\",\"quantity\":null}")]
    [InlineData("{\"slotId\":\"00000000-0000-0000-0000-000000000000\",\"quantity\":1}")]
    [InlineData("{\"slotId\":\"invalid-guid\",\"quantity\":1}")]
    [InlineData("{\"slotId\":\"$slotId\",\"quantity\":0}")]
    [InlineData("{\"slotId\":\"$slotId\",\"quantity\":-1}")]
    [InlineData("{\"slotId\":\"$slotId\",\"quantity\":-2147483648}")]
    [InlineData("{\"slotId\":\"$slotId\",\"quantity\":2147483648}")]
    [InlineData("{\"slotId\":\"$slotId\",\"quantity\":1.5}")]
    [InlineData("{\"slotId\":\"$slotId\",\"quantity\":\"invalid\"}")]
    public async Task Post_WhenRequestIsInvalid_ReturnsValidationProblemWithoutChangingData(string body) {
        // Arrange.
        await using var database = await postgres.CreateDatabaseAsync();
        var slot = await InsertSlotAsync(database, 10);
        await using var factory = new TestApiFactory(database.ConnectionString, "Development", false);
        using var client = CreateClient(factory);
        using var content = new StringContent(
            body.Replace("$slotId", slot.Id.ToString()),
            Encoding.UTF8,
            "application/json");

        // Act.
        using var response = await client.PostAsync("/api/orders", content);

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
    public async Task Post_WhenSlotIsMissing_ReturnsNotFoundWithoutChangingOtherSlots() {
        // Arrange.
        await using var database = await postgres.CreateDatabaseAsync();
        await InsertSlotAsync(database, 10);
        await using var factory = new TestApiFactory(database.ConnectionString, "Development", false);
        using var client = CreateClient(factory);

        // Act.
        using var response = await client.PostAsJsonAsync("/api/orders", new {
            slotId = Guid.NewGuid(),
            quantity = 1
        });

        // Assert.
        await AssertProblemAsync(response, HttpStatusCode.NotFound);

        await using var reader = database.CreateContext();
        Assert.Equal(0, await reader.Orders.CountAsync());
        Assert.Equal(0, (await reader.DeliverySlots.SingleAsync()).Reserved);
    }

    [Fact]
    public async Task Post_WhenQuantityExceedsCapacity_ReturnsConflictWithoutChangingData() {
        // Arrange.
        await using var database = await postgres.CreateDatabaseAsync();
        var slot = await InsertSlotAsync(database, 2);
        await using var factory = new TestApiFactory(database.ConnectionString, "Development", false);
        using var client = CreateClient(factory);

        // Act.
        using var response = await client.PostAsJsonAsync("/api/orders", new { slotId = slot.Id, quantity = 3 });

        // Assert.
        await AssertProblemAsync(response, HttpStatusCode.Conflict);

        await using var reader = database.CreateContext();
        Assert.Equal(0, await reader.Orders.CountAsync());
        Assert.Equal(0, (await reader.DeliverySlots.SingleAsync()).Reserved);
    }

    [Fact]
    public async Task Post_WhenLastCapacityIsUsed_RejectsNextOrderWithoutExtraReservation() {
        // Arrange.
        await using var database = await postgres.CreateDatabaseAsync();
        var slot = await InsertSlotAsync(database, 2);
        await using var factory = new TestApiFactory(database.ConnectionString, "Development", false);
        using var client = CreateClient(factory);

        using var created = await client.PostAsJsonAsync("/api/orders", new { slotId = slot.Id, quantity = 2 });
        // Act.
        using var rejected = await client.PostAsJsonAsync("/api/orders", new { slotId = slot.Id, quantity = 1 });

        // Assert.
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await AssertProblemAsync(rejected, HttpStatusCode.Conflict);

        await using var reader = database.CreateContext();
        Assert.Equal(1, await reader.Orders.CountAsync());
        Assert.Equal(2, (await reader.DeliverySlots.SingleAsync()).Reserved);
    }

    [Fact]
    public async Task Get_WhenOrderIsMissing_ReturnsNotFound() {
        // Arrange.
        await using var database = await postgres.CreateDatabaseAsync();
        await using var factory = new TestApiFactory(database.ConnectionString, "Development", false);
        using var client = CreateClient(factory);

        // Act.
        using var response = await client.GetAsync($"/api/orders/{Guid.NewGuid()}");

        // Assert.
        await AssertProblemAsync(response, HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("invalid-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Get_WhenIdIsInvalid_ReturnsValidationProblem(string id) {
        // Arrange.
        await using var database = await postgres.CreateDatabaseAsync();
        await using var factory = new TestApiFactory(database.ConnectionString, "Development", false);
        using var client = CreateClient(factory);

        // Act.
        using var response = await client.GetAsync($"/api/orders/{id}");

        // Assert.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();

        Assert.NotNull(problem);
        Assert.Equal(400, problem.Status);
        Assert.NotEmpty(problem.Errors);
    }

    private static HttpClient CreateClient(TestApiFactory factory) => factory.CreateClient(new WebApplicationFactoryClientOptions {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false
    });

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status) {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = Assert.IsType<ProblemDetails>(await response.Content.ReadFromJsonAsync<ProblemDetails>());

        Assert.Equal((int)status, problem.Status);
    }

    private static async Task<DeliverySlot> InsertSlotAsync(TestDatabase database, int capacity) {
        var startsAtUtc = new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero);
        var slot = new DeliverySlot(Guid.NewGuid(), capacity, startsAtUtc, startsAtUtc.AddHours(4));
        await using var context = database.CreateContext();
        context.DeliverySlots.Add(slot);
        await context.SaveChangesAsync();

        return slot;
    }
}
