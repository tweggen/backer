using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hannibal.Migrations
{
    /// <inheritdoc />
    public partial class AddOAuth2ToStorages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccessToken",
                table: "Storages",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "OAuth2Email",
                table: "Storages",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RefreshToken",
                table: "Storages",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccessToken",
                table: "Storages");

            migrationBuilder.DropColumn(
                name: "OAuth2Email",
                table: "Storages");

            migrationBuilder.DropColumn(
                name: "RefreshToken",
                table: "Storages");
        }
    }
}
