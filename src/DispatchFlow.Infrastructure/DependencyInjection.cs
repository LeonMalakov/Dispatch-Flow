using DispatchFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DispatchFlow.Infrastructure;

public static class DependencyInjection {
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string connectionString) {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));

        return services;
    }
}