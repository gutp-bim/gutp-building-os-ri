using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingOS.Shared.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "admin_audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    target_id = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    actor_sub = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    actor_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    result = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    detail = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_audit", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_admin_audit_subject_type_created_at",
                table: "admin_audit",
                columns: new[] { "subject_type", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_admin_audit_target_id_created_at",
                table: "admin_audit",
                columns: new[] { "target_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admin_audit");
        }
    }
}
