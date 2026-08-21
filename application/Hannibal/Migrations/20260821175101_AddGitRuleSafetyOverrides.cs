using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hannibal.Migrations
{
    /// <inheritdoc />
    public partial class AddGitRuleSafetyOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowAdopt",
                table: "Rules",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AllowUnsafeRefChange",
                table: "Rules",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowAdopt",
                table: "Rules");

            migrationBuilder.DropColumn(
                name: "AllowUnsafeRefChange",
                table: "Rules");
        }
    }
}
