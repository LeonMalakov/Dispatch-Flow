using DispatchFlow.Application.DeliverySlots;
using DispatchFlow.Application.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DispatchFlow.Infrastructure.Persistence.Configurations;

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order> {
    internal const string IDEMPOTENCY_KEY_INDEX = "ux_orders_idempotency_key";
    internal const string SLOT_FOREIGN_KEY = "fk_orders_delivery_slots_slot_id";

    public void Configure(EntityTypeBuilder<Order> builder) {
        builder.ToTable("orders", table => {
            table.HasCheckConstraint("ck_orders_quantity_positive", "quantity > 0");
            table.HasCheckConstraint("ck_orders_status", "status IN (0, 1)");
            table.HasCheckConstraint("ck_orders_idempotency_key", "idempotency_key IS NULL OR (length(idempotency_key) BETWEEN 1 AND 128 AND idempotency_key !~ '[^A-Za-z0-9_.:-]')");
        });

        builder.HasKey(order => order.Id).HasName("pk_orders");

        builder.Property(order => order.Id).HasColumnName("id").HasColumnType("uuid").ValueGeneratedNever();
        builder.Property(order => order.SlotId).HasColumnName("slot_id").HasColumnType("uuid").IsRequired();
        builder.Property(order => order.Quantity).HasColumnName("quantity").HasColumnType("integer").IsRequired();
        builder.Property(order => order.Status).HasColumnName("status").HasConversion<int>().HasColumnType("integer").IsRequired();
        builder.Property(order => order.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(order => order.IdempotencyKey).HasColumnName("idempotency_key")
            .HasMaxLength(IdempotencyKeyRules.MAX_LENGTH).UseCollation("C").IsRequired(false);

        builder.HasOne<DeliverySlot>()
            .WithMany()
            .HasForeignKey(order => order.SlotId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(SLOT_FOREIGN_KEY);

        builder.HasIndex(order => order.SlotId).HasDatabaseName("ix_orders_slot_id");
        builder.HasIndex(order => order.IdempotencyKey).IsUnique().HasDatabaseName(IDEMPOTENCY_KEY_INDEX);
    }
}
