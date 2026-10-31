using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FmCareerChallenge.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class NationalTeamStints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NationalTeamStints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CareerId = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    StartSeasonIndex = table.Column<int>(type: "integer", nullable: false),
                    EndSeasonIndex = table.Column<int>(type: "integer", nullable: true),
                    OfficialMatches = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NationalTeamStints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NationalTeamStints_Careers_CareerId",
                        column: x => x.CareerId,
                        principalTable: "Careers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NationalTeamStints_CareerId_StartSeasonIndex",
                table: "NationalTeamStints",
                columns: new[] { "CareerId", "StartSeasonIndex" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NationalTeamStints");
        }
    }
}
