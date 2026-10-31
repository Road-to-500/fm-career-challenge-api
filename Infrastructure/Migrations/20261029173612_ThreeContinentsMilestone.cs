using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FmCareerChallenge.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ThreeContinentsMilestone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ThreeContinentsMilestoneVersion",
                table: "Careers",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "ThreeContinentsSeasonIndex",
                table: "Careers",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Careers" WHERE "ThreeContinentsSeasonIndex" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Cannot remove recorded three-continent milestone data.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(
                name: "ThreeContinentsMilestoneVersion",
                table: "Careers");

            migrationBuilder.DropColumn(
                name: "ThreeContinentsSeasonIndex",
                table: "Careers");
        }
    }
}
