using DispatchFlow.Api.Observability;
using DispatchFlow.Application.Orders;
using DispatchFlow.Infrastructure;
using DispatchFlow.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);
builder.AddObservability();

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<OrderMetrics>();
builder.Services.AddScoped<OrderService>();
builder.Services.AddInfrastructure(
    builder.Configuration.GetConnectionString("DispatchFlow")
    ?? throw new InvalidOperationException(
        "Connection string 'DispatchFlow' is not configured. Set ConnectionStrings__DispatchFlow."));

builder.Services.AddOpenApi();
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("postgresql", tags: ["ready"]);

var app = builder.Build();

app.UseExceptionHandler(new ExceptionHandlerOptions {
    SuppressDiagnosticsCallback = _ => false
});

if (app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("Development:SeedData")) {
    await using var scope = app.Services.CreateAsyncScope();
    int insertedSlots = await DevelopmentDataSeeder.SeedAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    app.Logger.LogInformation("Development seed inserted {InsertedSlots} delivery slots.", insertedSlots);
}

if (app.Environment.IsDevelopment()) {
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health/live", new HealthCheckOptions {
    Predicate = _ => false
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions {
    Predicate = check => check.Tags.Contains("ready")
});

app.Run();

public partial class Program { }
