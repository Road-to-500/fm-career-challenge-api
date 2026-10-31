using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FmCareerChallenge.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class NationalTeamCountry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CountryCode",
                table: "NationalTeamStints",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "NationalTeamStints" WHERE "CountryCode" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Cannot remove recorded national team country data.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(
                name: "CountryCode",
                table: "NationalTeamStints");
        }
    }
}
