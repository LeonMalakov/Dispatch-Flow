using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DispatchFlow.Infrastructure.Persistence.Migrations {
    /// <inheritdoc />
    public partial class InitialCreate : Migration {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) {
            migrationBuilder.CreateTable(
                name: "delivery_slots",
                columns: table => new {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    capacity = table.Column<int>(type: "integer", nullable: false),
                    reserved = table.Column<int>(type: "integer", nullable: false),
                    starts_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table => {
                    table.PrimaryKey("pk_delivery_slots", x => x.id);
                    table.CheckConstraint("ck_delivery_slots_capacity_positive", "capacity > 0");
                    table.CheckConstraint("ck_delivery_slots_reserved_within_capacity", "reserved >= 0 AND reserved <= capacity");
                    table.CheckConstraint("ck_delivery_slots_time_range", "starts_at_utc < ends_at_utc");
                });

            migrationBuilder.CreateTable(
                name: "orders",
                columns: table => new {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    slot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table => {
                    table.PrimaryKey("pk_orders", x => x.id);
                    table.CheckConstraint("ck_orders_quantity_positive", "quantity > 0");
                    table.CheckConstraint("ck_orders_status", "status IN (0, 1)");
                    table.ForeignKey(
                        name: "fk_orders_delivery_slots_slot_id",
                        column: x => x.slot_id,
                        principalTable: "delivery_slots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_orders_slot_id",
                table: "orders",
                column: "slot_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) {
            migrationBuilder.DropTable(
                name: "orders");

            migrationBuilder.DropTable(
                name: "delivery_slots");
        }
    }
}