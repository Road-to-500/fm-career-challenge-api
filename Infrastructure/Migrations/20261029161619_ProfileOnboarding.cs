using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FmCareerChallenge.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ProfileOnboarding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasCompletedOnboarding",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);
            migrationBuilder.Sql("""UPDATE "AspNetUsers" SET "HasCompletedOnboarding" = TRUE;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HasCompletedOnboarding",
                table: "AspNetUsers");
        }
    }
}
