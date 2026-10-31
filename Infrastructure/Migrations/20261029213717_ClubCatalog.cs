using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FmCareerChallenge.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ClubCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ClubId",
                table: "CareerStints",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Clubs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    CountryCode = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Clubs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Clubs_Countries_CountryCode",
                        column: x => x.CountryCode,
                        principalTable: "Countries",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CareerStints_ClubId",
                table: "CareerStints",
                column: "ClubId");

            migrationBuilder.CreateIndex(
                name: "IX_Clubs_CountryCode_NormalizedName",
                table: "Clubs",
                columns: new[] { "CountryCode", "NormalizedName" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_CareerStints_Clubs_ClubId",
                table: "CareerStints",
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
                    IF EXISTS (SELECT 1 FROM "Clubs") THEN
                        RAISE EXCEPTION 'Cannot revert the club catalog while catalog entries exist.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_CareerStints_Clubs_ClubId",
                table: "CareerStints");

            migrationBuilder.DropTable(
                name: "Clubs");

            migrationBuilder.DropIndex(
                name: "IX_CareerStints_ClubId",
                table: "CareerStints");

            migrationBuilder.DropColumn(
                name: "ClubId",
                table: "CareerStints");
        }
    }
}
