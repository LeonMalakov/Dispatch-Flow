using DispatchFlow.Application.Orders;
using DispatchFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DispatchFlow.Infrastructure;

public static class DependencyInjection {
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string connectionString) {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString,
            npgsql => npgsql.ConfigureDataSource(dataSource => {
                dataSource.Name = "DispatchFlow";
                dataSource.ConfigureTracing(tracing => tracing
                    .ConfigureCommandEnrichmentCallback((activity, _) => activity.SetTag("db.query.text", null))
                    .ConfigureBatchEnrichmentCallback((activity, _) => activity.SetTag("db.query.text", null)));
            })));
        services.AddScoped<IOrderRepository, EfOrderRepository>();

        return services;
    }
}
