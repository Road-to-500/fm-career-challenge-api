using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FmCareerChallenge.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RequireNewCareerStart : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequiresStartingStatus",
                table: "Careers",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Careers" WHERE "RequiresStartingStatus") THEN
                        RAISE EXCEPTION 'Cannot remove the career start requirement while new careers use it.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(
                name: "RequiresStartingStatus",
                table: "Careers");
        }
    }
}
