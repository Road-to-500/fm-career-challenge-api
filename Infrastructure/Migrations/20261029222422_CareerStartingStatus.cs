using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FmCareerChallenge.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CareerStartingStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StartingStatus",
                table: "Careers",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "StartingStatusVersion",
                table: "Careers",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "StartingStintId",
                table: "Careers",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Careers_StartingStintId",
                table: "Careers",
                column: "StartingStintId");

            migrationBuilder.AddForeignKey(
                name: "FK_Careers_CareerStints_StartingStintId",
                table: "Careers",
                column: "StartingStintId",
                principalTable: "CareerStints",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Careers" WHERE "StartingStatus" IS NOT NULL OR "StartingStintId" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Cannot remove career starting status while recorded evidence exists.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_Careers_CareerStints_StartingStintId",
                table: "Careers");

            migrationBuilder.DropIndex(
                name: "IX_Careers_StartingStintId",
                table: "Careers");

            migrationBuilder.DropColumn(
                name: "StartingStatus",
                table: "Careers");

            migrationBuilder.DropColumn(
                name: "StartingStatusVersion",
                table: "Careers");

            migrationBuilder.DropColumn(
                name: "StartingStintId",
                table: "Careers");
        }
    }
}
