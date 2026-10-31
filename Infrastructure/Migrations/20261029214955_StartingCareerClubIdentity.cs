using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FmCareerChallenge.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class StartingCareerClubIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "StartingCareerClubId",
                table: "AchievementClaims",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AchievementClaims_StartingCareerClubId",
                table: "AchievementClaims",
                column: "StartingCareerClubId");

            migrationBuilder.AddForeignKey(
                name: "FK_AchievementClaims_Clubs_StartingCareerClubId",
                table: "AchievementClaims",
                column: "StartingCareerClubId",
                principalTable: "Clubs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "AchievementClaims" WHERE "StartingCareerClubId" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Cannot revert starting career club identities while linked claims exist.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_AchievementClaims_Clubs_StartingCareerClubId",
                table: "AchievementClaims");

            migrationBuilder.DropIndex(
                name: "IX_AchievementClaims_StartingCareerClubId",
                table: "AchievementClaims");

            migrationBuilder.DropColumn(
                name: "StartingCareerClubId",
                table: "AchievementClaims");
        }
    }
}
