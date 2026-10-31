using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FmCareerChallenge.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ChallengeCorrectionSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FinalRulesVersion",
                table: "Challenges",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FinalScoreSnapshot",
                table: "Challenges",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsCorrectionMode",
                table: "Challenges",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FinalRulesVersion",
                table: "Challenges");

            migrationBuilder.DropColumn(
                name: "FinalScoreSnapshot",
                table: "Challenges");

            migrationBuilder.DropColumn(
                name: "IsCorrectionMode",
                table: "Challenges");
        }
    }
}
