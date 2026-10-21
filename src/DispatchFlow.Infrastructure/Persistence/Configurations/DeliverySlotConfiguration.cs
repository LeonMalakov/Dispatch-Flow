using DispatchFlow.Application.DeliverySlots;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DispatchFlow.Infrastructure.Persistence.Configurations;

internal sealed class DeliverySlotConfiguration : IEntityTypeConfiguration<DeliverySlot> {
    public void Configure(EntityTypeBuilder<DeliverySlot> builder) {
        builder.ToTable("delivery_slots", table => {
            table.HasCheckConstraint("ck_delivery_slots_capacity_positive", "capacity > 0");
            table.HasCheckConstraint("ck_delivery_slots_reserved_within_capacity", "reserved >= 0 AND reserved <= capacity");
            table.HasCheckConstraint("ck_delivery_slots_time_range", "starts_at_utc < ends_at_utc");
        });

        builder.HasKey(slot => slot.Id).HasName("pk_delivery_slots");

        builder.Property(slot => slot.Id).HasColumnName("id").HasColumnType("uuid").ValueGeneratedNever();
        builder.Property(slot => slot.Capacity).HasColumnName("capacity").HasColumnType("integer").IsRequired();
        builder.Property(slot => slot.Reserved).HasColumnName("reserved").HasColumnType("integer").IsRequired();
        builder.Property(slot => slot.StartsAtUtc).HasColumnName("starts_at_utc").HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(slot => slot.EndsAtUtc).HasColumnName("ends_at_utc").HasColumnType("timestamp with time zone").IsRequired();
    }
}