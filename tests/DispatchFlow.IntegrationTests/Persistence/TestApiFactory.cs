using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DispatchFlow.IntegrationTests.Persistence;

internal sealed class TestApiFactory(string connectionString, string environment, bool? seedEnabled)
    : WebApplicationFactory<Program> {
    protected override void ConfigureWebHost(IWebHostBuilder builder) {
        builder.UseEnvironment(environment);
        builder.UseSetting("ConnectionStrings:DispatchFlow", connectionString);
        if (seedEnabled.HasValue) {
            builder.UseSetting("Development:SeedData", seedEnabled.Value.ToString());
        }
    }
}