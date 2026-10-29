using System.Net;
using System.Net.Sockets;
using DispatchFlow.IntegrationTests.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace DispatchFlow.IntegrationTests.Api;

public sealed class HealthCheckApiTests {
    [Theory]
    [InlineData("Development", "/health/live", HttpStatusCode.OK, "Healthy")]
    [InlineData("Production", "/health/live", HttpStatusCode.OK, "Healthy")]
    [InlineData("Development", "/health/ready", HttpStatusCode.ServiceUnavailable, "Unhealthy")]
    [InlineData("Production", "/health/ready", HttpStatusCode.ServiceUnavailable, "Unhealthy")]
    public async Task Get_WhenPostgreSqlIsUnavailable_ReturnsExpectedStatusWithoutDetails(
        string environment, string path, HttpStatusCode expectedStatus, string expectedBody) {
        // Arrange.
        // Hold an OS-assigned port without listening, so connections fail without a free-port race.
        using var unavailableEndpoint = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) {
            ExclusiveAddressUse = true
        };
        unavailableEndpoint.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)unavailableEndpoint.LocalEndPoint!).Port;
        var connectionString = new NpgsqlConnectionStringBuilder {
            Host = "127.0.0.1",
            Port = port,
            Database = "health_check_database",
            Username = "health_check_user",
            Password = "not-a-real-health-check-password",
            Pooling = false,
            Timeout = 1
        }.ConnectionString;

        await using var factory = new TestApiFactory(connectionString, environment, false);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

        // Act.
        using var response = await client.GetAsync(path);

        // Assert.
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(expectedBody, await response.Content.ReadAsStringAsync());
    }
}
