using System.Net;
using System.Net.Http.Json;
using DispatchFlow.Api.Contracts.Orders;
using DispatchFlow.Application.DeliverySlots;
using DispatchFlow.Application.Orders;
using DispatchFlow.IntegrationTests.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace DispatchFlow.IntegrationTests.Api;

[Collection("PostgreSQL")]
public sealed class OrderConcurrencyTests(PostgreSqlFixture postgres) {
    [Fact]
    public Task Post_WhenTwoRequestsCompeteForLastUnit_CreatesOnlyOneOrder() =>
        AssertConcurrentReservationsAsync(capacity: 3, initialQuantity: 2, requestCount: 2, quantity: 1,
            expectedCreated: 1, expectedReserved: 3);

    [Fact]
    public Task Post_WhenManyRequestsExceedCapacity_ReservesOnlyAvailableUnits() =>
        AssertConcurrentReservationsAsync(capacity: 10, initialQuantity: 0, requestCount: 30, quantity: 1,
            expectedCreated: 10, expectedReserved: 10);

    [Fact]
    public Task Post_WhenQuantityExceedsRemainingCapacity_DoesNotReservePartialQuantity() =>
        AssertConcurrentReservationsAsync(capacity: 5, initialQuantity: 0, requestCount: 12, quantity: 2,
            expectedCreated: 2, expectedReserved: 4);

    private async Task AssertConcurrentReservationsAsync(int capacity, int initialQuantity, int requestCount,
        int quantity, int expectedCreated, int expectedReserved) {

        // Arrange.
        await using var database = await postgres.CreateDatabaseAsync();
        var startsAtUtc = new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.Zero);
        var slot = new DeliverySlot(Guid.NewGuid(), capacity, startsAtUtc, startsAtUtc.AddHours(4));

        await using (var setup = database.CreateContext()) {
            setup.DeliverySlots.Add(slot);
            await setup.SaveChangesAsync();
        }

        await using var factory = new TestApiFactory(database.ConnectionString, "Development", false);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

        var expectedQuantities = new Dictionary<Guid, int>();
        if (initialQuantity > 0) {
            using var initialResponse = await client.PostAsJsonAsync("/api/orders", new {
                slotId = slot.Id,
                quantity = initialQuantity
            });

            Assert.Equal(HttpStatusCode.Created, initialResponse.StatusCode);
            var initialOrder = Assert.IsType<OrderResponse>(await initialResponse.Content.ReadFromJsonAsync<OrderResponse>());
            expectedQuantities.Add(initialOrder.Id, initialQuantity);
        }

        // Act.
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = Enumerable.Range(0, requestCount).Select(_ => PostAfterStartAsync()).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(requests);

        // Assert.
        var createdOrders = results
            .Where(result => result.Status == HttpStatusCode.Created)
            .Select(result => Assert.IsType<OrderResponse>(result.Order))
            .ToArray();

        Assert.Equal(expectedCreated, createdOrders.Length);
        Assert.Equal(requestCount - expectedCreated, results.Count(result => result.Status == HttpStatusCode.Conflict));
        Assert.All(createdOrders, order => {
            Assert.NotEqual(Guid.Empty, order.Id);
            Assert.Equal(slot.Id, order.SlotId);
            Assert.Equal(quantity, order.Quantity);
            Assert.Equal("Created", order.Status);
        });

        foreach (var order in createdOrders) {
            Assert.True(
                expectedQuantities.TryAdd(order.Id, order.Quantity),
                "Every successful request must create a distinct order.");
        }

        // Read committed database state with a fresh context.
        await using var reader = database.CreateContext();
        var storedSlot = await reader.DeliverySlots.AsNoTracking().SingleAsync();
        var storedOrders = await reader.Orders.AsNoTracking().ToListAsync();
        Assert.Equal(expectedReserved, storedSlot.Reserved);
        Assert.InRange(storedSlot.Reserved, 0, storedSlot.Capacity);
        Assert.Equal(expectedCreated + (initialQuantity > 0 ? 1 : 0), storedOrders.Count);

        Assert.Equal(
            expectedQuantities.Keys.OrderBy(id => id).ToArray(),
            storedOrders.Select(order => order.Id).OrderBy(id => id).ToArray());

        Assert.Equal(storedOrders.Sum(order => order.Quantity), storedSlot.Reserved);

        Assert.All(storedOrders, order => {
            Assert.Equal(slot.Id, order.SlotId);
            Assert.Equal(OrderStatus.Created, order.Status);
            Assert.Equal(expectedQuantities[order.Id], order.Quantity);
        });

        async Task<(HttpStatusCode Status, OrderResponse? Order)> PostAfterStartAsync() {
            await start.Task;
            using var response = await client.PostAsJsonAsync("/api/orders", new { slotId = slot.Id, quantity });
            Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict,
                $"Unexpected HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            var order = response.StatusCode == HttpStatusCode.Created
                ? await response.Content.ReadFromJsonAsync<OrderResponse>()
                : null;
            return (response.StatusCode, order);
        }
    }
}
