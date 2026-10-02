using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingOS.Shared.Migrations
{
    /// <inheritdoc />
    public partial class AddHealthEvent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "health_event",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    subject_id = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    raised_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    cleared_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    acknowledged_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    acknowledged_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    acknowledged_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    detail = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_health_event", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_health_event_cleared_at",
                table: "health_event",
                column: "cleared_at");

            migrationBuilder.CreateIndex(
                name: "IX_health_event_raised_at",
                table: "health_event",
                column: "raised_at");

            migrationBuilder.CreateIndex(
                name: "IX_health_event_subject_raised_at",
                table: "health_event",
                columns: new[] { "subject_type", "subject_id", "raised_at" });

            migrationBuilder.CreateIndex(
                name: "UX_health_event_open_subject_kind",
                table: "health_event",
                columns: new[] { "subject_type", "subject_id", "kind" },
                unique: true,
                filter: "cleared_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "health_event");
        }
    }
}
