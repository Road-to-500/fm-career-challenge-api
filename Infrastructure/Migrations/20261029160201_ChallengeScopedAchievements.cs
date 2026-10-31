using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FmCareerChallenge.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ChallengeScopedAchievements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Code",
                table: "AchievementDefinitions",
                type: "character varying(33)",
                maxLength: 33,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(4)",
                oldMaxLength: 4);

            migrationBuilder.AddColumn<Guid>(
                name: "ChallengeId",
                table: "AchievementDefinitions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "AchievementCode",
                table: "AchievementClaims",
                type: "character varying(33)",
                maxLength: 33,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(4)",
                oldMaxLength: 4);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G001",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G002",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G003",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G004",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G005",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G006",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G007",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G008",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G009",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G010",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G011",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G012",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G013",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G014",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G015",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G016",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G017",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G018",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G019",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G020",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G021",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G022",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G023",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G024",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G025",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G026",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G027",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G028",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G029",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G030",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G031",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G032",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G033",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G034",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G035",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G036",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G037",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G038",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G039",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G040",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G041",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G042",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G043",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G044",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G045",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G046",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G047",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G048",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G049",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G050",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G051",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G052",
                column: "ChallengeId",
                value: null);

            migrationBuilder.UpdateData(
                table: "AchievementDefinitions",
                keyColumn: "Code",
                keyValue: "G053",
                column: "ChallengeId",
                value: null);

            migrationBuilder.CreateIndex(
                name: "IX_AchievementDefinitions_ChallengeId",
                table: "AchievementDefinitions",
                column: "ChallengeId");

            migrationBuilder.AddForeignKey(
                name: "FK_AchievementDefinitions_Challenges_ChallengeId",
                table: "AchievementDefinitions",
                column: "ChallengeId",
                principalTable: "Challenges",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "AchievementDefinitions" WHERE "ChallengeId" IS NOT NULL)
                        OR EXISTS (SELECT 1 FROM "AchievementClaims" WHERE length("AchievementCode") > 4)
                        OR EXISTS (SELECT 1 FROM "ChallengeRules" WHERE length("AchievementCode") > 4)
                    THEN
                        RAISE EXCEPTION 'Cannot roll back scoped achievements while custom achievement data exists.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_AchievementDefinitions_Challenges_ChallengeId",
                table: "AchievementDefinitions");

            migrationBuilder.DropIndex(
                name: "IX_AchievementDefinitions_ChallengeId",
                table: "AchievementDefinitions");

            migrationBuilder.DropColumn(
                name: "ChallengeId",
                table: "AchievementDefinitions");

            migrationBuilder.AlterColumn<string>(
                name: "Code",
                table: "AchievementDefinitions",
                type: "character varying(4)",
                maxLength: 4,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(33)",
                oldMaxLength: 33);

            migrationBuilder.AlterColumn<string>(
                name: "AchievementCode",
                table: "AchievementClaims",
                type: "character varying(4)",
                maxLength: 4,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(33)",
                oldMaxLength: 33);
        }
    }
}
