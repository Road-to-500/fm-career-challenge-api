using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FmCareerChallenge.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AchievementClaimClubIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ClubId",
                table: "AchievementClaims",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AchievementClaims_ClubId",
                table: "AchievementClaims",
                column: "ClubId");

            migrationBuilder.AddForeignKey(
                name: "FK_AchievementClaims_Clubs_ClubId",
                table: "AchievementClaims",
                column: "ClubId",
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
                    IF EXISTS (SELECT 1 FROM "AchievementClaims" WHERE "ClubId" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Cannot revert achievement club identities while linked claims exist.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_AchievementClaims_Clubs_ClubId",
                table: "AchievementClaims");

            migrationBuilder.DropIndex(
                name: "IX_AchievementClaims_ClubId",
                table: "AchievementClaims");

            migrationBuilder.DropColumn(
                name: "ClubId",
                table: "AchievementClaims");
        }
    }
}
