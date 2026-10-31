using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FmCareerChallenge.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CareerMoveEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StartingCareerClub",
                table: "AchievementClaims",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StartingCareerCountry",
                table: "AchievementClaims",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "AchievementClaims"
                        WHERE "StartingCareerClub" IS NOT NULL OR "StartingCareerCountry" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Cannot remove career move evidence while recorded data exists.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(
                name: "StartingCareerClub",
                table: "AchievementClaims");

            migrationBuilder.DropColumn(
                name: "StartingCareerCountry",
                table: "AchievementClaims");
        }
    }
}
