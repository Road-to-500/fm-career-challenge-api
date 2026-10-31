using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FmCareerChallenge.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RuleFrequencyOverride : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FrequencyOverride",
                table: "ChallengeRules",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "ChallengeRules" WHERE "FrequencyOverride" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Cannot remove rule frequencies while recorded customizations exist.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(
                name: "FrequencyOverride",
                table: "ChallengeRules");
        }
    }
}
