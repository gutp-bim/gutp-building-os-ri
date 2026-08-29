using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingOS.Shared.Migrations
{
    /// <inheritdoc />
    public partial class AddGatewayPointListCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "gateway_pointlist_cache",
                columns: table => new
                {
                    gateway_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    etag = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    point_count = table.Column<int>(type: "integer", nullable: false),
                    materialized_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gateway_pointlist_cache", x => x.gateway_id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "gateway_pointlist_cache");
        }
    }
}
