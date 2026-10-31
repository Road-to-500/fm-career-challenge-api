using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace FmCareerChallenge.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AchievementDefinitions",
                columns: table => new
                {
                    Code = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Frequency = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DefaultPoints = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AchievementDefinitions", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    AvatarUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastLoginAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: true),
                    SecurityStamp = table.Column<string>(type: "text", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true),
                    PhoneNumber = table.Column<string>(type: "text", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Challenges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Mode = table.Column<string>(type: "text", nullable: false),
                    TargetPoints = table.Column<int>(type: "integer", nullable: false),
                    StartSeasonYear = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    WinningParticipantId = table.Column<Guid>(type: "uuid", nullable: true),
                    WinningSeasonIndex = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Challenges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserNotifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChallengeId = table.Column<Guid>(type: "uuid", nullable: true),
                    Type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    RelatedEntityId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReadAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserNotifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    ProviderKey = table.Column<string>(type: "text", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "text", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ActivityEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChallengeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParticipantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ClubName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    SeasonIndex = table.Column<int>(type: "integer", nullable: true),
                    Points = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActivityEvents_Challenges_ChallengeId",
                        column: x => x.ChallengeId,
                        principalTable: "Challenges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: true),
                    ChallengeId = table.Column<Guid>(type: "uuid", nullable: true),
                    EntityType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    EntityId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Action = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    OldData = table.Column<string>(type: "text", nullable: true),
                    NewData = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuditLogs_Challenges_ChallengeId",
                        column: x => x.ChallengeId,
                        principalTable: "Challenges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ChallengeInvitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChallengeId = table.Column<Guid>(type: "uuid", nullable: false),
                    InvitedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    InviteeEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RedeemedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChallengeInvitations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChallengeInvitations_Challenges_ChallengeId",
                        column: x => x.ChallengeId,
                        principalTable: "Challenges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ChallengeParticipants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChallengeId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    JoinedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChallengeParticipants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChallengeParticipants_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ChallengeParticipants_Challenges_ChallengeId",
                        column: x => x.ChallengeId,
                        principalTable: "Challenges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ChallengeRules",
                columns: table => new
                {
                    ChallengeId = table.Column<Guid>(type: "uuid", nullable: false),
                    AchievementCode = table.Column<string>(type: "text", nullable: false),
                    Points = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChallengeRules", x => new { x.ChallengeId, x.AchievementCode });
                    table.ForeignKey(
                        name: "FK_ChallengeRules_Challenges_ChallengeId",
                        column: x => x.ChallengeId,
                        principalTable: "Challenges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScoreEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChallengeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParticipantId = table.Column<Guid>(type: "uuid", nullable: false),
                    SeasonIndex = table.Column<int>(type: "integer", nullable: false),
                    SourceType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SourceKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Points = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RuleVersion = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScoreEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScoreEvents_Challenges_ChallengeId",
                        column: x => x.ChallengeId,
                        principalTable: "Challenges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Careers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ParticipantId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrainerName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    CurrentClubName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    CurrentCountryCode = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Careers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Careers_ChallengeParticipants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "ChallengeParticipants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AchievementClaims",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CareerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParticipantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AchievementCode = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    OccurrenceKey = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    SeasonIndex = table.Column<int>(type: "integer", nullable: false),
                    ClubName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    CountryCode = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AchievementClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AchievementClaims_Careers_CareerId",
                        column: x => x.CareerId,
                        principalTable: "Careers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CareerSeasons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CareerId = table.Column<Guid>(type: "uuid", nullable: false),
                    SeasonIndex = table.Column<int>(type: "integer", nullable: false),
                    StartYear = table.Column<int>(type: "integer", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LeagueMatches = table.Column<int>(type: "integer", nullable: false),
                    LeagueWins = table.Column<int>(type: "integer", nullable: false),
                    LeagueDraws = table.Column<int>(type: "integer", nullable: false),
                    LeagueLosses = table.Column<int>(type: "integer", nullable: false),
                    LeaguePoints = table.Column<int>(type: "integer", nullable: false),
                    GoalsFor = table.Column<int>(type: "integer", nullable: false),
                    GoalsAgainst = table.Column<int>(type: "integer", nullable: false),
                    LeaguePosition = table.Column<int>(type: "integer", nullable: true),
                    LargestOutgoingTransfer = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CareerSeasons", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CareerSeasons_Careers_CareerId",
                        column: x => x.CareerId,
                        principalTable: "Careers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CareerStints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CareerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClubName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    CountryCode = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    CountryName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    StartSeasonIndex = table.Column<int>(type: "integer", nullable: false),
                    EndSeasonIndex = table.Column<int>(type: "integer", nullable: true),
                    InitialDivisionLevel = table.Column<int>(type: "integer", nullable: false),
                    OfficialMatches = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CareerStints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CareerStints_Careers_CareerId",
                        column: x => x.CareerId,
                        principalTable: "Careers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Transfers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CareerId = table.Column<Guid>(type: "uuid", nullable: false),
                    SeasonIndex = table.Column<int>(type: "integer", nullable: false),
                    ClubName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    PlayerName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Direction = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FixedAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Transfers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Transfers_Careers_CareerId",
                        column: x => x.CareerId,
                        principalTable: "Careers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "AchievementDefinitions",
                columns: new[] { "Code", "Category", "DefaultPoints", "Description", "Frequency", "IsEnabled", "Name" },
                values: new object[,]
                {
                    { "G001", "Domestic", 18, "Awarded for first division champion.", "Season", true, "First Division Champion" },
                    { "G002", "Domestic", 12, "Awarded for second division champion.", "Season", true, "Second Division Champion" },
                    { "G003", "Domestic", 8, "Awarded for third division champion.", "Season", true, "Third Division Champion" },
                    { "G004", "Domestic", 5, "Awarded for fourth division champion or lower.", "Season", true, "Fourth Division Champion or Lower" },
                    { "G005", "Domestic", 7, "Awarded for promotion to the first division.", "Season", true, "Promotion to the First Division" },
                    { "G006", "Domestic", 5, "Awarded for promotion to the second division.", "Season", true, "Promotion to the Second Division" },
                    { "G007", "Domestic", 3, "Awarded for promotion to a lower division.", "Season", true, "Promotion to a Lower Division" },
                    { "G008", "Domestic", 10, "Awarded for national cup winner.", "Season", true, "National Cup Winner" },
                    { "G009", "Domestic", 6, "Awarded for league cup winner.", "Season", true, "League Cup Winner" },
                    { "G010", "Domestic", 3, "Awarded for national super cup winner.", "Season", true, "National Super Cup Winner" },
                    { "G011", "Domestic", 4, "Awarded for first-division survival after promotion.", "ClubSeason", true, "First-Division Survival After Promotion" },
                    { "G012", "Domestic", 5, "Awarded for first-division runner-up.", "Season", true, "First-Division Runner-Up" },
                    { "G013", "Domestic", 7, "Awarded for domestic double.", "Season", true, "Domestic Double" },
                    { "G014", "Domestic", 10, "Awarded for unbeaten league season.", "Season", true, "Unbeaten League Season" },
                    { "G015", "Domestic", 8, "Awarded for national cup winner with a lower-division club.", "Season", true, "National Cup Winner with a Lower-Division Club" },
                    { "G016", "Domestic", 6, "Awarded for top four with a newly promoted club.", "Season", true, "Top Four with a Newly Promoted Club" },
                    { "G017", "Domestic", 8, "Awarded for two consecutive promotions at one club.", "PerClub", true, "Two Consecutive Promotions at One Club" },
                    { "G018", "Domestic", 12, "Awarded for three consecutive promotions at one club.", "PerClub", true, "Three Consecutive Promotions at One Club" },
                    { "G019", "Domestic", 10, "Awarded for first-division champion after promotion.", "Season", true, "First-Division Champion After Promotion" },
                    { "G020", "Continental", 30, "Awarded for top continental club competition winner.", "Season", true, "Top Continental Club Competition Winner" },
                    { "G021", "Continental", 20, "Awarded for second-tier continental competition winner.", "Season", true, "Second-Tier Continental Competition Winner" },
                    { "G022", "Continental", 15, "Awarded for third-tier continental competition winner.", "Season", true, "Third-Tier Continental Competition Winner" },
                    { "G023", "Continental", 4, "Awarded for continental super cup winner.", "Season", true, "Continental Super Cup Winner" },
                    { "G024", "Continental", 12, "Awarded for club world cup winner.", "Season", true, "Club World Cup Winner" },
                    { "G025", "Continental", 6, "Awarded for first continental qualification with a club.", "PerClub", true, "First Continental Qualification with a Club" },
                    { "G026", "Continental", 2, "Awarded for repeat continental qualification with a club.", "ClubSeason", true, "Repeat Continental Qualification with a Club" },
                    { "G027", "Continental", 4, "Awarded for first top-tier continental qualification with a club.", "PerClub", true, "First Top-Tier Continental Qualification with a Club" },
                    { "G028", "Continental", 4, "Awarded for champions league quarter-finalist.", "Season", true, "Champions League Quarter-Finalist" },
                    { "G029", "Continental", 7, "Awarded for champions league semi-finalist.", "Season", true, "Champions League Semi-Finalist" },
                    { "G030", "Continental", 10, "Awarded for champions league runner-up.", "Season", true, "Champions League Runner-Up" },
                    { "G031", "Continental", 7, "Awarded for europa league runner-up.", "Season", true, "Europa League Runner-Up" },
                    { "G032", "Continental", 10, "Awarded for continental trophies with two different clubs.", "Once", true, "Continental Trophies with Two Different Clubs" },
                    { "G033", "International", 8, "Awarded for twenty official matches as a national team manager.", "Once", true, "Twenty Official Matches as a National Team Manager" },
                    { "G034", "International", 25, "Awarded for world cup winner.", "Season", true, "World Cup Winner" },
                    { "G035", "International", 18, "Awarded for continental national team champion.", "Season", true, "Continental National Team Champion" },
                    { "G036", "International", 10, "Awarded for world cup semi-finalist.", "Season", true, "World Cup Semi-Finalist" },
                    { "G037", "Career", 5, "Awarded for new club career move.", "PerClub", true, "New Club Career Move" },
                    { "G038", "Career", 6, "Awarded for first first-division contract.", "Once", true, "First First-Division Contract" },
                    { "G039", "Career", 4, "Awarded for first step up to a higher division.", "Once", true, "First Step Up to a Higher Division" },
                    { "G040", "Career", 5, "Awarded for first major trophy with a club.", "PerClub", true, "First Major Trophy with a Club" },
                    { "G041", "Career", 5, "Awarded for twenty matches in a new country.", "PerCountry", true, "Twenty Matches in a New Country" },
                    { "G042", "Career", 8, "Awarded for managed three different clubs.", "Once", true, "Managed Three Different Clubs" },
                    { "G043", "Career", 8, "Awarded for managed five different clubs.", "Once", true, "Managed Five Different Clubs" },
                    { "G044", "Career", 10, "Awarded for managed eight different clubs.", "Once", true, "Managed Eight Different Clubs" },
                    { "G045", "Career", 10, "Awarded for first-division champion in two countries.", "Once", true, "First-Division Champion in Two Countries" },
                    { "G046", "Career", 10, "Awarded for first-division champion in three countries.", "Once", true, "First-Division Champion in Three Countries" },
                    { "G047", "Career", 8, "Awarded for managed in four different countries.", "Once", true, "Managed in Four Different Countries" },
                    { "G048", "Career", 8, "Awarded for won ten major trophies.", "Once", true, "Won Ten Major Trophies" },
                    { "G049", "Career", 8, "Awarded for first-division champion with two clubs.", "Once", true, "First-Division Champion with Two Clubs" },
                    { "G050", "Career", 12, "Awarded for promoted one club from the third to first division.", "PerClub", true, "Promoted One Club from the Third to First Division" },
                    { "G051", "Career", 4, "Awarded for manager of the year.", "Season", true, "Manager of the Year" },
                    { "G052", "Career", 8, "Awarded for won five first-division championships.", "Once", true, "Won Five First-Division Championships" },
                    { "G053", "Career", 10, "Awarded for managed across three continents.", "Once", true, "Managed Across Three Continents" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_AchievementClaims_CareerId",
                table: "AchievementClaims",
                column: "CareerId");

            migrationBuilder.CreateIndex(
                name: "IX_AchievementClaims_ParticipantId_AchievementCode_OccurrenceK~",
                table: "AchievementClaims",
                columns: new[] { "ParticipantId", "AchievementCode", "OccurrenceKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AchievementClaims_ParticipantId_SeasonIndex",
                table: "AchievementClaims",
                columns: new[] { "ParticipantId", "SeasonIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityEvents_ChallengeId_CreatedAt",
                table: "ActivityEvents",
                columns: new[] { "ChallengeId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetRoleClaims_RoleId",
                table: "AspNetRoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "AspNetRoles",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                table: "AspNetUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserRoles_RoleId",
                table: "AspNetUserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_ChallengeId_CreatedAt",
                table: "AuditLogs",
                columns: new[] { "ChallengeId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Careers_ParticipantId",
                table: "Careers",
                column: "ParticipantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CareerSeasons_CareerId_SeasonIndex",
                table: "CareerSeasons",
                columns: new[] { "CareerId", "SeasonIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CareerStints_CareerId_ClubName",
                table: "CareerStints",
                columns: new[] { "CareerId", "ClubName" });

            migrationBuilder.CreateIndex(
                name: "IX_ChallengeInvitations_ChallengeId_InviteeEmail",
                table: "ChallengeInvitations",
                columns: new[] { "ChallengeId", "InviteeEmail" });

            migrationBuilder.CreateIndex(
                name: "IX_ChallengeInvitations_TokenHash",
                table: "ChallengeInvitations",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChallengeParticipants_ChallengeId_UserId",
                table: "ChallengeParticipants",
                columns: new[] { "ChallengeId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChallengeParticipants_UserId",
                table: "ChallengeParticipants",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ChallengeRules_ChallengeId_VersionNumber",
                table: "ChallengeRules",
                columns: new[] { "ChallengeId", "VersionNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_Challenges_CreatedByUserId",
                table: "Challenges",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ScoreEvents_ChallengeId_ParticipantId_SourceType_SourceKey",
                table: "ScoreEvents",
                columns: new[] { "ChallengeId", "ParticipantId", "SourceType", "SourceKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Transfers_CareerId",
                table: "Transfers",
                column: "CareerId");

            migrationBuilder.CreateIndex(
                name: "IX_UserNotifications_UserId_CreatedAt",
                table: "UserNotifications",
                columns: new[] { "UserId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AchievementClaims");

            migrationBuilder.DropTable(
                name: "AchievementDefinitions");

            migrationBuilder.DropTable(
                name: "ActivityEvents");

            migrationBuilder.DropTable(
                name: "AspNetRoleClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins");

            migrationBuilder.DropTable(
                name: "AspNetUserRoles");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens");

            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "CareerSeasons");

            migrationBuilder.DropTable(
                name: "CareerStints");

            migrationBuilder.DropTable(
                name: "ChallengeInvitations");

            migrationBuilder.DropTable(
                name: "ChallengeRules");

            migrationBuilder.DropTable(
                name: "ScoreEvents");

            migrationBuilder.DropTable(
                name: "Transfers");

            migrationBuilder.DropTable(
                name: "UserNotifications");

            migrationBuilder.DropTable(
                name: "AspNetRoles");

            migrationBuilder.DropTable(
                name: "Careers");

            migrationBuilder.DropTable(
                name: "ChallengeParticipants");

            migrationBuilder.DropTable(
                name: "AspNetUsers");

            migrationBuilder.DropTable(
                name: "Challenges");
        }
    }
}
