using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FmCareerChallenge.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CompetitionCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CompetitionId",
                table: "AchievementClaims",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ChallengeCompetitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChallengeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Tier = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChallengeCompetitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChallengeCompetitions_Challenges_ChallengeId",
                        column: x => x.ChallengeId,
                        principalTable: "Challenges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AchievementClaims_CompetitionId",
                table: "AchievementClaims",
                column: "CompetitionId");

            migrationBuilder.CreateIndex(
                name: "IX_ChallengeCompetitions_ChallengeId_NormalizedName",
                table: "ChallengeCompetitions",
                columns: new[] { "ChallengeId", "NormalizedName" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AchievementClaims_ChallengeCompetitions_CompetitionId",
                table: "AchievementClaims",
                column: "CompetitionId",
                principalTable: "ChallengeCompetitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "ChallengeCompetitions")
                        OR EXISTS (SELECT 1 FROM "AchievementClaims" WHERE "CompetitionId" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Cannot remove the competition catalog while recorded data exists.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_AchievementClaims_ChallengeCompetitions_CompetitionId",
                table: "AchievementClaims");

            migrationBuilder.DropTable(
                name: "ChallengeCompetitions");

            migrationBuilder.DropIndex(
                name: "IX_AchievementClaims_CompetitionId",
                table: "AchievementClaims");

            migrationBuilder.DropColumn(
                name: "CompetitionId",
                table: "AchievementClaims");
        }
    }
}
