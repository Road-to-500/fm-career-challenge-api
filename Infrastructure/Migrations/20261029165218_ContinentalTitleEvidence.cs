using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FmCareerChallenge.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ContinentalTitleEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FirstContinentalTitleClub",
                table: "AchievementClaims",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FirstContinentalTitleCountry",
                table: "AchievementClaims",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FirstContinentalTitleSeason",
                table: "AchievementClaims",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WonContinentalTitlesWithBothClubs",
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
                        WHERE "FirstContinentalTitleClub" IS NOT NULL OR "FirstContinentalTitleCountry" IS NOT NULL
                           OR "FirstContinentalTitleSeason" IS NOT NULL OR "WonContinentalTitlesWithBothClubs" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Cannot remove continental title evidence while recorded data exists.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(
                name: "FirstContinentalTitleClub",
                table: "AchievementClaims");

            migrationBuilder.DropColumn(
                name: "FirstContinentalTitleCountry",
                table: "AchievementClaims");

            migrationBuilder.DropColumn(
                name: "FirstContinentalTitleSeason",
                table: "AchievementClaims");

            migrationBuilder.DropColumn(
                name: "WonContinentalTitlesWithBothClubs",
                table: "AchievementClaims");
        }
    }
}
