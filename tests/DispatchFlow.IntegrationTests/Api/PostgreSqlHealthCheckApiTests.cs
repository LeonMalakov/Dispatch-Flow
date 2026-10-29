using System.Net;
using DispatchFlow.IntegrationTests.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DispatchFlow.IntegrationTests.Api;

[Collection("PostgreSQL")]
public sealed class PostgreSqlHealthCheckApiTests(PostgreSqlFixture postgres) {
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Get_WhenDatabaseIsAvailableWithoutMigrations_ReturnsHealthy(string path) {
        // Arrange.
        await using var database = await postgres.CreateDatabaseAsync(applyMigrations: false);
        await using var factory = new TestApiFactory(database.ConnectionString, "Production", false);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

        // Act.
        using var response = await client.GetAsync(path);

        // Assert.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
}
