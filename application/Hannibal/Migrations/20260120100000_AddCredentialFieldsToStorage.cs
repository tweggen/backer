using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hannibal.Migrations
{
    /// <inheritdoc />
    public partial class AddCredentialFieldsToStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Username",
                table: "Storages",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Password",
                table: "Storages",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Host",
                table: "Storages",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Port",
                table: "Storages",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Domain",
                table: "Storages",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Username",
                table: "Storages");

            migrationBuilder.DropColumn(
                name: "Password",
                table: "Storages");

            migrationBuilder.DropColumn(
                name: "Host",
                table: "Storages");

            migrationBuilder.DropColumn(
                name: "Port",
                table: "Storages");

            migrationBuilder.DropColumn(
                name: "Domain",
                table: "Storages");
        }
    }
}
