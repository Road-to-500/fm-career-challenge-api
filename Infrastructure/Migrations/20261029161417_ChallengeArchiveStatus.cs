using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FmCareerChallenge.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ChallengeArchiveStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StatusBeforeArchive",
                table: "Challenges",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Challenges" WHERE "StatusBeforeArchive" IS NOT NULL)
                    THEN
                        RAISE EXCEPTION 'Cannot roll back while challenges have archived state.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(
                name: "StatusBeforeArchive",
                table: "Challenges");
        }
    }
}
