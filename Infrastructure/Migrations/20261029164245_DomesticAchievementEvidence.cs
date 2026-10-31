using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FmCareerChallenge.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DomesticAchievementEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CupWonInLowerDivision",
                table: "AchievementClaims",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DomesticCupMatchesManaged",
                table: "AchievementClaims",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WonFirstDivisionTitle",
                table: "AchievementClaims",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WonLeagueTitleUnbeaten",
                table: "AchievementClaims",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WonMajorNationalCup",
                table: "AchievementClaims",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "AchievementClaims"
                        WHERE "WonFirstDivisionTitle" IS NOT NULL OR "WonMajorNationalCup" IS NOT NULL
                           OR "WonLeagueTitleUnbeaten" IS NOT NULL OR "CupWonInLowerDivision" IS NOT NULL
                           OR "DomesticCupMatchesManaged" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Cannot remove domestic achievement evidence while recorded data exists.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(
                name: "CupWonInLowerDivision",
                table: "AchievementClaims");

            migrationBuilder.DropColumn(
                name: "DomesticCupMatchesManaged",
                table: "AchievementClaims");

            migrationBuilder.DropColumn(
                name: "WonFirstDivisionTitle",
                table: "AchievementClaims");

            migrationBuilder.DropColumn(
                name: "WonLeagueTitleUnbeaten",
                table: "AchievementClaims");

            migrationBuilder.DropColumn(
                name: "WonMajorNationalCup",
                table: "AchievementClaims");
        }
    }
}
