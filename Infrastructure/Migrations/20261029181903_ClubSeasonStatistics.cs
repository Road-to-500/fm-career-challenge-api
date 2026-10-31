using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FmCareerChallenge.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ClubSeasonStatistics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "UsesClubBreakdown",
                table: "CareerSeasons",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ClubSeasonStatistics",
                columns: table => new
                {
                    CareerSeasonId = table.Column<Guid>(type: "uuid", nullable: false),
                    CareerStintId = table.Column<Guid>(type: "uuid", nullable: false),
                    DivisionLevel = table.Column<int>(type: "integer", nullable: false),
                    OfficialMatches = table.Column<int>(type: "integer", nullable: false),
                    LeagueMatches = table.Column<int>(type: "integer", nullable: false),
                    LeagueWins = table.Column<int>(type: "integer", nullable: false),
                    LeagueDraws = table.Column<int>(type: "integer", nullable: false),
                    LeagueLosses = table.Column<int>(type: "integer", nullable: false),
                    LeaguePoints = table.Column<int>(type: "integer", nullable: false),
                    GoalsFor = table.Column<int>(type: "integer", nullable: false),
                    GoalsAgainst = table.Column<int>(type: "integer", nullable: false),
                    LeaguePosition = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClubSeasonStatistics", x => new { x.CareerSeasonId, x.CareerStintId });
                    table.ForeignKey(
                        name: "FK_ClubSeasonStatistics_CareerSeasons_CareerSeasonId",
                        column: x => x.CareerSeasonId,
                        principalTable: "CareerSeasons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClubSeasonStatistics_CareerStints_CareerStintId",
                        column: x => x.CareerStintId,
                        principalTable: "CareerStints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClubSeasonStatistics_CareerStintId",
                table: "ClubSeasonStatistics",
                column: "CareerStintId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "ClubSeasonStatistics") THEN
                        RAISE EXCEPTION 'Cannot revert club season statistics while detailed records exist.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropTable(
                name: "ClubSeasonStatistics");

            migrationBuilder.DropColumn(
                name: "UsesClubBreakdown",
                table: "CareerSeasons");
        }
    }
}
