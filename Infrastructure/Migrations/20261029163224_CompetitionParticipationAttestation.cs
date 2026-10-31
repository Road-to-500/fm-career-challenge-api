using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FmCareerChallenge.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CompetitionParticipationAttestation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "InChargeAtCompetitionEnd",
                table: "AchievementClaims",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ManagedCompetitionMatches",
                table: "AchievementClaims",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ManagedDecidingMatch",
                table: "AchievementClaims",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TotalCompetitionMatches",
                table: "AchievementClaims",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "AchievementClaims"
                        WHERE "InChargeAtCompetitionEnd" IS NOT NULL OR "ManagedCompetitionMatches" IS NOT NULL
                           OR "ManagedDecidingMatch" IS NOT NULL OR "TotalCompetitionMatches" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Cannot remove competition participation attestations while recorded data exists.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(
                name: "InChargeAtCompetitionEnd",
                table: "AchievementClaims");

            migrationBuilder.DropColumn(
                name: "ManagedCompetitionMatches",
                table: "AchievementClaims");

            migrationBuilder.DropColumn(
                name: "ManagedDecidingMatch",
                table: "AchievementClaims");

            migrationBuilder.DropColumn(
                name: "TotalCompetitionMatches",
                table: "AchievementClaims");
        }
    }
}
