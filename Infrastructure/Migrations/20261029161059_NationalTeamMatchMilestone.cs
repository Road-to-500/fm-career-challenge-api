using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FmCareerChallenge.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class NationalTeamMatchMilestone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "NationalTeam20MatchSeasonIndex",
                table: "Careers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "NationalTeamMilestoneVersion",
                table: "Careers",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Careers" WHERE "NationalTeam20MatchSeasonIndex" IS NOT NULL)
                    THEN
                        RAISE EXCEPTION 'Cannot roll back while national team milestone seasons exist.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(
                name: "NationalTeam20MatchSeasonIndex",
                table: "Careers");

            migrationBuilder.DropColumn(
                name: "NationalTeamMilestoneVersion",
                table: "Careers");
        }
    }
}
