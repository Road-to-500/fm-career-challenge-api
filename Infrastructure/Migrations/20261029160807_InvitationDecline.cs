using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FmCareerChallenge.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InvitationDecline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeclinedAt",
                table: "ChallengeInvitations",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "ChallengeInvitations" WHERE "DeclinedAt" IS NOT NULL)
                    THEN
                        RAISE EXCEPTION 'Cannot roll back while declined invitations exist.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(
                name: "DeclinedAt",
                table: "ChallengeInvitations");
        }
    }
}
