using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingOS.Shared.Migrations
{
    /// <inheritdoc />
    public partial class AddResourceGroupCreatedBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CreatedBy",
                table: "resource_groups",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "resource_groups");
        }
    }
}
