using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FmCareerChallenge.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class VersionedChallengeRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_ChallengeRules",
                table: "ChallengeRules");

            migrationBuilder.AddColumn<int>(
                name: "EffectiveSeasonIndex",
                table: "ChallengeRules",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddPrimaryKey(
                name: "PK_ChallengeRules",
                table: "ChallengeRules",
                columns: new[] { "ChallengeId", "VersionNumber", "AchievementCode" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_ChallengeRules",
                table: "ChallengeRules");

            migrationBuilder.DropColumn(
                name: "EffectiveSeasonIndex",
                table: "ChallengeRules");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ChallengeRules",
                table: "ChallengeRules",
                columns: new[] { "ChallengeId", "AchievementCode" });
        }
    }
}
