using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingOS.Shared.Migrations
{
    /// <inheritdoc />
    public partial class AddHealthEventClearReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "clear_reason",
                table: "health_event",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "clear_reason",
                table: "health_event");
        }
    }
}
