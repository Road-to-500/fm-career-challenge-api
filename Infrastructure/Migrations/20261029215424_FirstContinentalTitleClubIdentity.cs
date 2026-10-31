using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FmCareerChallenge.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FirstContinentalTitleClubIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "FirstContinentalTitleClubId",
                table: "AchievementClaims",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AchievementClaims_FirstContinentalTitleClubId",
                table: "AchievementClaims",
                column: "FirstContinentalTitleClubId");

            migrationBuilder.AddForeignKey(
                name: "FK_AchievementClaims_Clubs_FirstContinentalTitleClubId",
                table: "AchievementClaims",
                column: "FirstContinentalTitleClubId",
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
                    IF EXISTS (SELECT 1 FROM "AchievementClaims" WHERE "FirstContinentalTitleClubId" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Cannot revert first continental title club identities while linked claims exist.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_AchievementClaims_Clubs_FirstContinentalTitleClubId",
                table: "AchievementClaims");

            migrationBuilder.DropIndex(
                name: "IX_AchievementClaims_FirstContinentalTitleClubId",
                table: "AchievementClaims");

            migrationBuilder.DropColumn(
                name: "FirstContinentalTitleClubId",
                table: "AchievementClaims");
        }
    }
}
