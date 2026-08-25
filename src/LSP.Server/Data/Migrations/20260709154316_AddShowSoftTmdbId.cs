using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LSP.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddShowSoftTmdbId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SoftTmdbId",
                table: "Shows",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SoftTmdbId",
                table: "Shows");
        }
    }
}
