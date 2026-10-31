using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FmCareerChallenge.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TransferDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Transfers_CareerId",
                table: "Transfers");

            migrationBuilder.AddColumn<string>(
                name: "CounterpartyName",
                table: "Transfers",
                type: "character varying(120)",
                maxLength: 120,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "Transfers",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "Transfers",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "IX_Transfers_CareerId_SeasonIndex",
                table: "Transfers",
                columns: new[] { "CareerId", "SeasonIndex" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Transfers_CareerId_SeasonIndex",
                table: "Transfers");

            migrationBuilder.DropColumn(
                name: "CounterpartyName",
                table: "Transfers");

            migrationBuilder.DropColumn(
                name: "Notes",
                table: "Transfers");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Transfers");

            migrationBuilder.CreateIndex(
                name: "IX_Transfers_CareerId",
                table: "Transfers",
                column: "CareerId");
        }
    }
}
