using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FmCareerChallenge.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InternationalResultEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CompetitionResultKind",
                table: "AchievementClaims",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CompetitionResultStage",
                table: "AchievementClaims",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "AchievementClaims"
                        WHERE "CompetitionResultKind" IS NOT NULL OR "CompetitionResultStage" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Cannot remove international result evidence while recorded data exists.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(
                name: "CompetitionResultKind",
                table: "AchievementClaims");

            migrationBuilder.DropColumn(
                name: "CompetitionResultStage",
                table: "AchievementClaims");
        }
    }
}
