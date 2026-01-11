using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hannibal.Migrations
{
    /// <inheritdoc />
    public partial class AddClientSecretToStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClientSecret",
                table: "Storages",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClientSecret",
                table: "Storages");
        }
    }
}
