using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FmCareerChallenge.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ContinentalParticipationEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "FirstTopTierMainPhase",
                table: "AchievementClaims",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PriorContinentalMainPhase",
                table: "AchievementClaims",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReachedContinentalMainPhase",
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
                        WHERE "ReachedContinentalMainPhase" IS NOT NULL OR "PriorContinentalMainPhase" IS NOT NULL
                           OR "FirstTopTierMainPhase" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Cannot remove continental participation evidence while recorded data exists.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(
                name: "FirstTopTierMainPhase",
                table: "AchievementClaims");

            migrationBuilder.DropColumn(
                name: "PriorContinentalMainPhase",
                table: "AchievementClaims");

            migrationBuilder.DropColumn(
                name: "ReachedContinentalMainPhase",
                table: "AchievementClaims");
        }
    }
}
