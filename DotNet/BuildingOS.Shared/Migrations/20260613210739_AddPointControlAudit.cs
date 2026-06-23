using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingOS.Shared.Migrations
{
    /// <inheritdoc />
    public partial class AddPointControlAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // IF NOT EXISTS guards are intentional: environments that ran the legacy Flyway
            // migration V002__point_control_audit.sql already have this table; skip creation
            // rather than failing. New postgres:16 deployments go through the normal path.
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS point_control_audit (
                    id           UUID        PRIMARY KEY,
                    point_id     TEXT,
                    request      JSONB       NOT NULL,
                    result       JSONB,
                    created_at   TIMESTAMPTZ NOT NULL,
                    completed_at TIMESTAMPTZ
                );
                CREATE INDEX IF NOT EXISTS ""IX_point_control_audit_point_id_created_at""
                    ON point_control_audit (point_id, created_at);
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "point_control_audit");
        }
    }
}
