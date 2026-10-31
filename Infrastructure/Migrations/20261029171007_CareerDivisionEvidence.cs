using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FmCareerChallenge.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CareerDivisionEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "FirstDivisionExperience",
                table: "AchievementClaims",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HigherDivisionClubMove",
                table: "AchievementClaims",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NewDivisionLevel",
                table: "AchievementClaims",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PreviousBestDivisionLevel",
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
                        WHERE "FirstDivisionExperience" IS NOT NULL OR "HigherDivisionClubMove" IS NOT NULL
                           OR "NewDivisionLevel" IS NOT NULL OR "PreviousBestDivisionLevel" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Cannot remove career division evidence while recorded data exists.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(
                name: "FirstDivisionExperience",
                table: "AchievementClaims");

            migrationBuilder.DropColumn(
                name: "HigherDivisionClubMove",
                table: "AchievementClaims");

            migrationBuilder.DropColumn(
                name: "NewDivisionLevel",
                table: "AchievementClaims");

            migrationBuilder.DropColumn(
                name: "PreviousBestDivisionLevel",
                table: "AchievementClaims");
        }
    }
}
