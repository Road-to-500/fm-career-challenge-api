using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FmCareerChallenge.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PromotionAchievementAttestation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AvoidedRelegation",
                table: "AchievementClaims",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CompetitionLeaguePosition",
                table: "AchievementClaims",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "FirstSeasonAfterPromotion",
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
                        WHERE "FirstSeasonAfterPromotion" IS NOT NULL OR "AvoidedRelegation" IS NOT NULL
                           OR "CompetitionLeaguePosition" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Cannot remove promoted-club achievement attestations while recorded data exists.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(
                name: "AvoidedRelegation",
                table: "AchievementClaims");

            migrationBuilder.DropColumn(
                name: "CompetitionLeaguePosition",
                table: "AchievementClaims");

            migrationBuilder.DropColumn(
                name: "FirstSeasonAfterPromotion",
                table: "AchievementClaims");
        }
    }
}
