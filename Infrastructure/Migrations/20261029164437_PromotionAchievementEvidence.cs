using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FmCareerChallenge.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PromotionAchievementEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PromotionDestinationLevel",
                table: "AchievementClaims",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PromotionStreakStartSeason",
                table: "AchievementClaims",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PromotionWithoutTitle",
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
                        WHERE "PromotionDestinationLevel" IS NOT NULL OR "PromotionWithoutTitle" IS NOT NULL
                           OR "PromotionStreakStartSeason" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Cannot remove promotion evidence while recorded data exists.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(
                name: "PromotionDestinationLevel",
                table: "AchievementClaims");

            migrationBuilder.DropColumn(
                name: "PromotionStreakStartSeason",
                table: "AchievementClaims");

            migrationBuilder.DropColumn(
                name: "PromotionWithoutTitle",
                table: "AchievementClaims");
        }
    }
}
