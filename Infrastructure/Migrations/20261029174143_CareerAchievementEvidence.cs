using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FmCareerChallenge.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CareerAchievementEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "FirstMajorClubTrophy",
                table: "AchievementClaims",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "OfficialManagerOfYear",
                table: "AchievementClaims",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "StayedWithClubFromThirdToFirst",
                table: "AchievementClaims",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ThirdDivisionStartSeason",
                table: "AchievementClaims",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TrophyNotSuperCup",
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
                        WHERE "FirstMajorClubTrophy" IS NOT NULL OR "TrophyNotSuperCup" IS NOT NULL
                           OR "ThirdDivisionStartSeason" IS NOT NULL OR "StayedWithClubFromThirdToFirst" IS NOT NULL
                           OR "OfficialManagerOfYear" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Cannot remove career achievement evidence while recorded data exists.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(
                name: "FirstMajorClubTrophy",
                table: "AchievementClaims");

            migrationBuilder.DropColumn(
                name: "OfficialManagerOfYear",
                table: "AchievementClaims");

            migrationBuilder.DropColumn(
                name: "StayedWithClubFromThirdToFirst",
                table: "AchievementClaims");

            migrationBuilder.DropColumn(
                name: "ThirdDivisionStartSeason",
                table: "AchievementClaims");

            migrationBuilder.DropColumn(
                name: "TrophyNotSuperCup",
                table: "AchievementClaims");
        }
    }
}
