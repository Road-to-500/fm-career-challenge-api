using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FmCareerChallenge.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class NationalTeamSeasonMatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NationalTeamSeasonMatches",
                columns: table => new
                {
                    NationalTeamStintId = table.Column<Guid>(type: "uuid", nullable: false),
                    SeasonIndex = table.Column<int>(type: "integer", nullable: false),
                    OfficialMatches = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NationalTeamSeasonMatches", x => new { x.NationalTeamStintId, x.SeasonIndex });
                    table.ForeignKey(
                        name: "FK_NationalTeamSeasonMatches_NationalTeamStints_NationalTeamSt~",
                        column: x => x.NationalTeamStintId,
                        principalTable: "NationalTeamStints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "NationalTeamSeasonMatches") THEN
                        RAISE EXCEPTION 'Cannot revert national team season matches while records exist.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropTable(
                name: "NationalTeamSeasonMatches");
        }
    }
}
