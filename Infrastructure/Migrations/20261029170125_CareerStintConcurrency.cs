using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FmCareerChallenge.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CareerStintConcurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "CareerStints",
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
                    IF EXISTS (SELECT 1 FROM "CareerStints" WHERE "Version" <> 0) THEN
                        RAISE EXCEPTION 'Cannot remove club stint versions after a stint has been edited.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(
                name: "Version",
                table: "CareerStints");
        }
    }
}
