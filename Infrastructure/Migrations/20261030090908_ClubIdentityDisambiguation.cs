using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FmCareerChallenge.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ClubIdentityDisambiguation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Clubs_CountryCode_NormalizedName",
                table: "Clubs");

            migrationBuilder.AddColumn<string>(
                name: "IdentityLabel",
                table: "Clubs",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "NormalizedIdentityLabel",
                table: "Clubs",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_Clubs_CountryCode_NormalizedName_NormalizedIdentityLabel",
                table: "Clubs",
                columns: new[] { "CountryCode", "NormalizedName", "NormalizedIdentityLabel" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Clubs_CountryCode_NormalizedName_NormalizedIdentityLabel",
                table: "Clubs");

            migrationBuilder.DropColumn(
                name: "IdentityLabel",
                table: "Clubs");

            migrationBuilder.DropColumn(
                name: "NormalizedIdentityLabel",
                table: "Clubs");

            migrationBuilder.CreateIndex(
                name: "IX_Clubs_CountryCode_NormalizedName",
                table: "Clubs",
                columns: new[] { "CountryCode", "NormalizedName" },
                unique: true);
        }
    }
}
