using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FmCareerChallenge.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RuleDisplayOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CategoryOverride",
                table: "ChallengeRules",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DescriptionOverride",
                table: "ChallengeRules",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NameOverride",
                table: "ChallengeRules",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "ChallengeRules" WHERE "NameOverride" IS NOT NULL
                        OR "DescriptionOverride" IS NOT NULL OR "CategoryOverride" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Cannot remove rule display overrides while recorded customizations exist.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(
                name: "CategoryOverride",
                table: "ChallengeRules");

            migrationBuilder.DropColumn(
                name: "DescriptionOverride",
                table: "ChallengeRules");

            migrationBuilder.DropColumn(
                name: "NameOverride",
                table: "ChallengeRules");
        }
    }
}
