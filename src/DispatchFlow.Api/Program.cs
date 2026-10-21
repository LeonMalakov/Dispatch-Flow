using DispatchFlow.Infrastructure;
using DispatchFlow.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
builder.Services.AddInfrastructure(
    builder.Configuration.GetConnectionString("DispatchFlow")
    ?? throw new InvalidOperationException(
        "Connection string 'DispatchFlow' is not configured. Set ConnectionStrings__DispatchFlow."));
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("Development:SeedData")) {
    await using var scope = app.Services.CreateAsyncScope();
    int insertedSlots = await DevelopmentDataSeeder.SeedAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    app.Logger.LogInformation("Development seed inserted {InsertedSlots} delivery slots.", insertedSlots);
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment()) {
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();

public partial class Program { }