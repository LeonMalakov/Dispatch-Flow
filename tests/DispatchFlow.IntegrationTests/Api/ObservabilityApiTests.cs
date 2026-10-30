using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using DispatchFlow.IntegrationTests.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using OpenTelemetry.Trace;

namespace DispatchFlow.IntegrationTests.Api;

public sealed class ObservabilityApiTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Api_WhenOtlpIsDisabledOrUnavailable_StillServesRequests(bool exportEnabled) {
        // Arrange.
        using var unavailableEndpoint = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) {
            ExclusiveAddressUse = true
        };
        unavailableEndpoint.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)unavailableEndpoint.LocalEndPoint!).Port;
        string endpoint = exportEnabled ? $"http://127.0.0.1:{port}" : string.Empty;

        await using var template = new TestApiFactory(
            "Host=127.0.0.1;Database=unused;Username=unused", "Production", false);
        await using var factory = template.WithWebHostBuilder(builder =>
            builder.UseSetting("OTEL_EXPORTER_OTLP_ENDPOINT", endpoint));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

        // Act.
        using var invalidOrder = await client.PostAsJsonAsync("/api/orders", new { });
        // Exercise export failure before the next request, without waiting for a batch timer.
        factory.Services.GetRequiredService<TracerProvider>().ForceFlush(1000);
        using var live = await client.GetAsync("/health/live");

        // Assert.
        Assert.Equal(HttpStatusCode.BadRequest, invalidOrder.StatusCode);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("Healthy", await live.Content.ReadAsStringAsync());
        Assert.Contains(factory.Services.GetServices<ILoggerProvider>(), provider => provider is ConsoleLoggerProvider);
    }
}
