using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DispatchFlow.Infrastructure.Persistence.Migrations {
    /// <inheritdoc />
    public partial class AddOrderIdempotencyKey : Migration {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) {
            migrationBuilder.AddColumn<string>(
                name: "idempotency_key",
                table: "orders",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true,
                collation: "C");

            migrationBuilder.CreateIndex(
                name: "ux_orders_idempotency_key",
                table: "orders",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_orders_idempotency_key",
                table: "orders",
                sql: "idempotency_key IS NULL OR (length(idempotency_key) BETWEEN 1 AND 128 AND idempotency_key !~ '[^A-Za-z0-9_.:-]')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) {
            migrationBuilder.DropIndex(
                name: "ux_orders_idempotency_key",
                table: "orders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_orders_idempotency_key",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "idempotency_key",
                table: "orders");
        }
    }
}
