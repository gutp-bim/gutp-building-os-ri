using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingOS.Shared.Migrations
{
    /// <inheritdoc />
    public partial class InitialPostgres : Migration
    {
        // Greenfield OSS initial schema on the shared PostgreSQL/TimescaleDB
        // instance. Replaces the former MySQL (Pomelo) migration set; no data
        // migration is performed (OSS reference repo, no production MySQL data).
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "resource_groups",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resource_groups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "resource_id_mappings",
                columns: table => new
                {
                    HashedId = table.Column<string>(type: "character varying(56)", maxLength: 56, nullable: false),
                    ResourceType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    OriginalId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resource_id_mappings", x => x.HashedId);
                });

            migrationBuilder.CreateTable(
                name: "group_resource_items",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    GroupId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ResourceType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ResourceId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_resource_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_group_resource_items_resource_groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "resource_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_group_resource_items_GroupId_ResourceType_ResourceId",
                table: "group_resource_items",
                columns: new[] { "GroupId", "ResourceType", "ResourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_group_resource_items_ResourceType_ResourceId",
                table: "group_resource_items",
                columns: new[] { "ResourceType", "ResourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_resource_groups_Name",
                table: "resource_groups",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_resource_id_mappings_ResourceType",
                table: "resource_id_mappings",
                column: "ResourceType");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "group_resource_items");

            migrationBuilder.DropTable(
                name: "resource_id_mappings");

            migrationBuilder.DropTable(
                name: "resource_groups");
        }
    }
}
