using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LSP.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMatchAliasSeasonCacheParseCacheConfidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "Confidence",
                table: "ParseCaches",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedQuery",
                table: "ParseCaches",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MatchAliases",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    TmdbId = table.Column<int>(type: "INTEGER", nullable: false),
                    MediaType = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MatchAliases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TmdbSeasonCaches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TmdbId = table.Column<int>(type: "INTEGER", nullable: false),
                    Season = table.Column<int>(type: "INTEGER", nullable: false),
                    Data = table.Column<string>(type: "TEXT", nullable: true),
                    FetchedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TmdbSeasonCaches", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MatchAliases_Key",
                table: "MatchAliases",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TmdbSeasonCaches_TmdbId_Season",
                table: "TmdbSeasonCaches",
                columns: new[] { "TmdbId", "Season" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MatchAliases");

            migrationBuilder.DropTable(
                name: "TmdbSeasonCaches");

            migrationBuilder.DropColumn(
                name: "Confidence",
                table: "ParseCaches");

            migrationBuilder.DropColumn(
                name: "NormalizedQuery",
                table: "ParseCaches");
        }
    }
}
