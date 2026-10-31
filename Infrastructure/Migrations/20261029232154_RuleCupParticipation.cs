using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FmCareerChallenge.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RuleCupParticipation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MinimumCupMatchesOverride",
                table: "ChallengeRules",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "ChallengeRules" WHERE "MinimumCupMatchesOverride" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Cannot remove cup participation settings while recorded customizations exist.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(
                name: "MinimumCupMatchesOverride",
                table: "ChallengeRules");
        }
    }
}
