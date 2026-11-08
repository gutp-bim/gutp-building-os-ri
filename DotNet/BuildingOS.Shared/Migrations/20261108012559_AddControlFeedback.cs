using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingOS.Shared.Migrations
{
    /// <inheritdoc />
    public partial class AddControlFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "feedback_completed_at",
                table: "point_control_audit",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "feedback_status",
                table: "point_control_audit",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "feedback_value",
                table: "point_control_audit",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "feedback_completed_at",
                table: "point_control_audit");

            migrationBuilder.DropColumn(
                name: "feedback_status",
                table: "point_control_audit");

            migrationBuilder.DropColumn(
                name: "feedback_value",
                table: "point_control_audit");
        }
    }
}
