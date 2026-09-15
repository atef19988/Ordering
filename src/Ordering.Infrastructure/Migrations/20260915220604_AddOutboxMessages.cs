using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ordering.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false),
                    type = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    aggregate_id = table.Column<long>(type: "bigint", nullable: false),
                    payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    attempt_count = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    claimed_by = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    claimed_until = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    processed_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    last_error = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                    table.CheckConstraint("ck_outbox_payload_json", "ISJSON([payload]) = 1");
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_due",
                table: "outbox_messages",
                columns: new[] { "status", "next_attempt_at" },
                filter: "[status] IN ('Pending', 'Processing')");

            migrationBuilder.CreateIndex(
                name: "ux_outbox_order_created",
                table: "outbox_messages",
                columns: new[] { "type", "aggregate_id" },
                unique: true,
                filter: "[type] = 'order.created'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox_messages");
        }
    }
}
