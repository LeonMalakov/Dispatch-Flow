using DispatchFlow.Application.DeliverySlots;
using DispatchFlow.Application.Orders;
using DispatchFlow.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace DispatchFlow.Infrastructure.Persistence;

public sealed class AppDbContext : DbContext {
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<DeliverySlot> DeliverySlots => Set<DeliverySlot>();
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfiguration(new DeliverySlotConfiguration());
        modelBuilder.ApplyConfiguration(new OrderConfiguration());
    }
}