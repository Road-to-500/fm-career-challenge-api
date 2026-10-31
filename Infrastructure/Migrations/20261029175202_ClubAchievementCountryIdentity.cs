using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FmCareerChallenge.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ClubAchievementCountryIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "OccurrenceKey",
                table: "AchievementClaims",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(180)",
                oldMaxLength: 180);

            migrationBuilder.Sql("""
                UPDATE "AchievementClaims" AS claim
                SET "OccurrenceKey" = 'club-v2:'
                    || replace(encode(convert_to(claim."OccurrenceKey", 'UTF8'), 'base64'), chr(10), '')
                    || ':'
                    || replace(encode(convert_to(upper(btrim(coalesce(claim."CountryCode", ''))), 'UTF8'), 'base64'), chr(10), '')
                FROM "AchievementDefinitions" AS definition
                WHERE claim."AchievementCode" = definition."Code"
                  AND definition."Frequency" IN ('PerClub', 'ClubSeason');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM "AchievementClaims" AS claim
                        JOIN "AchievementDefinitions" AS definition ON claim."AchievementCode" = definition."Code"
                        WHERE definition."Frequency" IN ('PerClub', 'ClubSeason')
                          AND claim."OccurrenceKey" NOT LIKE 'club-v2:%'
                    ) THEN
                        RAISE EXCEPTION 'Cannot revert club identity: an occurrence key has an unexpected format.';
                    END IF;
                    IF EXISTS (
                        SELECT 1
                        FROM "AchievementClaims" AS claim
                        JOIN "AchievementDefinitions" AS definition ON claim."AchievementCode" = definition."Code"
                        WHERE definition."Frequency" IN ('PerClub', 'ClubSeason')
                        GROUP BY claim."ParticipantId", claim."AchievementCode", split_part(claim."OccurrenceKey", ':', 2)
                        HAVING count(*) > 1
                    ) THEN
                        RAISE EXCEPTION 'Cannot revert club identity: claims for clubs with the same name would conflict.';
                    END IF;
                END $$;
                UPDATE "AchievementClaims" AS claim
                SET "OccurrenceKey" = convert_from(decode(split_part(claim."OccurrenceKey", ':', 2), 'base64'), 'UTF8')
                FROM "AchievementDefinitions" AS definition
                WHERE claim."AchievementCode" = definition."Code"
                  AND definition."Frequency" IN ('PerClub', 'ClubSeason');
                """);
            migrationBuilder.AlterColumn<string>(
                name: "OccurrenceKey",
                table: "AchievementClaims",
                type: "character varying(180)",
                maxLength: 180,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(1000)",
                oldMaxLength: 1000);
        }
    }
}
