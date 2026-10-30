using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace DispatchFlow.IntegrationTests.Persistence;

internal sealed class TestApiFactory(string connectionString, string environment, bool? seedEnabled,
    Action<IServiceCollection>? configureServices = null)
    : WebApplicationFactory<Program> {
    protected override void ConfigureWebHost(IWebHostBuilder builder) {
        builder.UseEnvironment(environment);
        builder.UseSetting("OTEL_EXPORTER_OTLP_ENDPOINT", string.Empty);
        builder.UseSetting("ConnectionStrings:DispatchFlow", connectionString);
        if (seedEnabled.HasValue) {
            builder.UseSetting("Development:SeedData", seedEnabled.Value.ToString());
        }

        if (configureServices is not null) {
            builder.ConfigureTestServices(configureServices);
        }
    }
}
