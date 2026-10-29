using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Data;
using FmCareerChallenge.Api.Domain;
using FmCareerChallenge.Api.Infrastructure;
using FmCareerChallenge.Api.Realtime;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.MicrosoftAccount;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("PostgreSql")
    ?? "Host=localhost;Port=5432;Database=fm_career_challenge;Username=postgres;Password=postgres";
var frontendOrigin = builder.Configuration["Frontend:Origin"] ?? "http://localhost:5173";

builder.Services.AddDbContext<ChallengeDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddScoped<ScoreRecalculator>();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddSignalR();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "fmcc.csrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(frontendOrigin)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
});

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.SignIn.RequireConfirmedEmail = false;
        options.Lockout.MaxFailedAccessAttempts = 10;
    })
    .AddRoles<IdentityRole<Guid>>()
    .AddEntityFrameworkStores<ChallengeDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme)
    .AddIdentityCookies();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "fmcc.session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
    options.SlidingExpiration = true;
    options.ExpireTimeSpan = TimeSpan.FromDays(14);
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
});

var googleClientId = builder.Configuration["Authentication:Google:ClientId"];
var googleClientSecret = builder.Configuration["Authentication:Google:ClientSecret"];
if (!string.IsNullOrWhiteSpace(googleClientId) && !string.IsNullOrWhiteSpace(googleClientSecret))
{
    builder.Services.AddAuthentication().AddGoogle(GoogleDefaults.AuthenticationScheme, options =>
    {
        options.ClientId = googleClientId;
        options.ClientSecret = googleClientSecret;
        options.SignInScheme = IdentityConstants.ExternalScheme;
        options.Scope.Add("profile");
        options.SaveTokens = false;
    });
}

var microsoftClientId = builder.Configuration["Authentication:Microsoft:ClientId"];
var microsoftClientSecret = builder.Configuration["Authentication:Microsoft:ClientSecret"];
if (!string.IsNullOrWhiteSpace(microsoftClientId) && !string.IsNullOrWhiteSpace(microsoftClientSecret))
{
    builder.Services.AddAuthentication().AddMicrosoftAccount(MicrosoftAccountDefaults.AuthenticationScheme, options =>
    {
        options.ClientId = microsoftClientId;
        options.ClientSecret = microsoftClientSecret;
        options.SignInScheme = IdentityConstants.ExternalScheme;
        options.SaveTokens = false;
    });
}

builder.Services.AddAuthorization();

var app = builder.Build();

app.UseExceptionHandler();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    var method = context.Request.Method;
    var isMutation = method is not ("GET" or "HEAD" or "OPTIONS" or "TRACE");
    if (context.Request.Path.StartsWithSegments("/api")
        && isMutation
        && !context.Request.Path.StartsWithSegments("/api/auth/external-callback"))
    {
        try
        {
            await context.RequestServices.GetRequiredService<IAntiforgery>()
                .ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new
            {
                title = "The security token is missing or invalid.",
                status = StatusCodes.Status400BadRequest,
            });
            return;
        }
    }

    await next();
});

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();

var auth = app.MapGroup("/api/auth").AllowAnonymous();
auth.MapGet("/csrf", (HttpContext context, IAntiforgery antiforgery) =>
{
    var tokens = antiforgery.GetAndStoreTokens(context);
    return Results.Ok(new { requestToken = tokens.RequestToken });
});
auth.MapGet("/google", (IConfiguration configuration) =>
{
    if (string.IsNullOrWhiteSpace(configuration["Authentication:Google:ClientId"])
        || string.IsNullOrWhiteSpace(configuration["Authentication:Google:ClientSecret"]))
    {
        return Results.Problem(
            "Google sign-in is not configured. Set Authentication__Google__ClientId and Authentication__Google__ClientSecret.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Challenge(
        new AuthenticationProperties { RedirectUri = "/api/auth/external-callback" },
        [GoogleDefaults.AuthenticationScheme]);
});
auth.MapGet("/microsoft", (IConfiguration configuration) =>
{
    if (string.IsNullOrWhiteSpace(configuration["Authentication:Microsoft:ClientId"])
        || string.IsNullOrWhiteSpace(configuration["Authentication:Microsoft:ClientSecret"]))
    {
        return Results.Problem(
            "Microsoft sign-in is not configured. Set Authentication__Microsoft__ClientId and Authentication__Microsoft__ClientSecret.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Challenge(
        new AuthenticationProperties { RedirectUri = "/api/auth/external-callback" },
        [MicrosoftAccountDefaults.AuthenticationScheme]);
});
auth.MapGet("/external-callback", async (
    HttpContext context,
    SignInManager<ApplicationUser> signInManager,
    UserManager<ApplicationUser> userManager,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var externalInfo = await signInManager.GetExternalLoginInfoAsync();
    if (externalInfo is null)
    {
        return Results.Problem("External sign-in could not be verified.", statusCode: StatusCodes.Status401Unauthorized);
    }

    var user = await userManager.FindByLoginAsync(externalInfo.LoginProvider, externalInfo.ProviderKey);
    if (user is null)
    {
        var email = externalInfo.Principal.FindFirstValue(ClaimTypes.Email);
        if (string.IsNullOrWhiteSpace(email))
        {
            await signInManager.SignOutAsync();
            return Results.Problem("The identity provider did not return an email address.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (await userManager.FindByEmailAsync(email) is not null)
        {
            await signInManager.SignOutAsync();
            return Results.Problem(
                "An account already uses this email with another sign-in provider. Sign in with that provider before linking accounts.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var displayName = externalInfo.Principal.FindFirstValue(ClaimTypes.Name);
        user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? email.Split('@')[0] : displayName,
            AvatarUrl = externalInfo.Principal.FindFirstValue("picture"),
            CreatedAt = DateTimeOffset.UtcNow,
            LastLoginAt = DateTimeOffset.UtcNow,
        };
        var createResult = await userManager.CreateAsync(user);
        if (!createResult.Succeeded)
        {
            await signInManager.SignOutAsync();
            return Results.Problem(
                string.Join("; ", createResult.Errors.Select(error => error.Description)),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var linkResult = await userManager.AddLoginAsync(user, externalInfo);
        if (!linkResult.Succeeded)
        {
            await userManager.DeleteAsync(user);
            await signInManager.SignOutAsync();
            return Results.Problem(
                string.Join("; ", linkResult.Errors.Select(error => error.Description)),
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    user.LastLoginAt = DateTimeOffset.UtcNow;
    var updateResult = await userManager.UpdateAsync(user);
    if (!updateResult.Succeeded)
    {
    return Results.Problem(
        string.Join("; ", updateResult.Errors.Select(error => error.Description)),
        statusCode: StatusCodes.Status500InternalServerError);
    }

    await signInManager.SignInAsync(user, isPersistent: true);
    await context.SignOutAsync(IdentityConstants.ExternalScheme);
    var frontendUrl = configuration["Frontend:BaseUrl"] ?? "http://localhost:5173";
    return Results.Redirect($"{frontendUrl.TrimEnd('/')}/dashboard");
});
auth.MapGet("/me", (ClaimsPrincipal principal) =>
    Results.Ok(new { id = principal.FindFirstValue(ClaimTypes.NameIdentifier), displayName = principal.Identity?.Name }));
auth.MapPost("/logout", async (SignInManager<ApplicationUser> signInManager) =>
{
    await signInManager.SignOutAsync();
    return Results.NoContent();
}).RequireAuthorization();

var me = app.MapGroup("/api/me").RequireAuthorization();
me.MapGet("/", async (ClaimsPrincipal principal, UserManager<ApplicationUser> users) =>
{
    var user = await users.GetUserAsync(principal);
    return user is null
        ? Results.Unauthorized()
        : Results.Ok(new { id = user.Id, displayName = user.DisplayName, email = user.Email, avatarUrl = user.AvatarUrl });
});
me.MapGet("/challenges", async (
    ClaimsPrincipal principal,
    ChallengeDbContext database,
    CancellationToken cancellationToken) =>
{
    var userId = GetUserId(principal);
    var results = await (
        from participant in database.ChallengeParticipants
        join challenge in database.Challenges on participant.ChallengeId equals challenge.Id
        join career in database.Careers on participant.Id equals career.ParticipantId into careers
        from career in careers.DefaultIfEmpty()
        where participant.UserId == userId
        orderby challenge.CreatedAt descending
        select new
        {
            ChallengeId = challenge.Id,
            ChallengeName = challenge.Name,
            Status = challenge.Status.ToString(),
            challenge.TargetPoints,
            ParticipantId = participant.Id,
            TrainerName = career == null ? null : career.TrainerName,
            challenge.WinningParticipantId,
            challenge.CreatedAt,
        }).ToListAsync(cancellationToken);

    var challengeIds = results.Select(item => item.ChallengeId).Distinct().ToArray();
    var participantRows = await (
        from participant in database.ChallengeParticipants
        join user in database.Users on participant.UserId equals user.Id
        join career in database.Careers on participant.Id equals career.ParticipantId into careers
        from career in careers.DefaultIfEmpty()
        where challengeIds.Contains(participant.ChallengeId)
        select new
        {
            participant.ChallengeId,
            participant.Id,
            user.DisplayName,
            TrainerName = career == null ? null : career.TrainerName,
        }).ToListAsync(cancellationToken);
    var participantIds = participantRows.Select(item => item.Id).ToArray();
    var points = await database.ScoreEvents
        .Where(item => participantIds.Contains(item.ParticipantId) && item.Status == "Confirmed")
        .GroupBy(item => item.ParticipantId)
        .Select(group => new { ParticipantId = group.Key, Points = group.Sum(item => item.Points) })
        .ToDictionaryAsync(item => item.ParticipantId, item => item.Points, cancellationToken);
    var latestActivity = await database.ActivityEvents
        .Where(item => challengeIds.Contains(item.ChallengeId))
        .GroupBy(item => item.ChallengeId)
        .Select(group => new { ChallengeId = group.Key, LastActivityAt = group.Max(item => item.CreatedAt) })
        .ToDictionaryAsync(item => item.ChallengeId, item => item.LastActivityAt, cancellationToken);

    var response = results.Select(item => new
    {
        id = item.ChallengeId,
        name = item.ChallengeName,
        status = item.Status,
        targetPoints = item.TargetPoints,
        myScore = points.GetValueOrDefault(item.ParticipantId),
        opponentScore = participantRows
            .Where(other => other.ChallengeId == item.ChallengeId && other.Id != item.ParticipantId)
            .Select(other => points.GetValueOrDefault(other.Id))
            .FirstOrDefault(),
        myTrainerName = item.TrainerName,
        opponentName = participantRows
            .Where(other => other.ChallengeId == item.ChallengeId && other.Id != item.ParticipantId)
            .Select(other => other.TrainerName ?? other.DisplayName)
            .FirstOrDefault(),
        currentSeason = (string?)null,
        lastActivityAt = latestActivity.GetValueOrDefault(item.ChallengeId, item.CreatedAt),
        winningParticipantId = item.WinningParticipantId,
    }).ToArray();
    return Results.Ok(response);
});
me.MapGet("/invitations", async (
    ClaimsPrincipal principal,
    UserManager<ApplicationUser> users,
    ChallengeDbContext database,
    CancellationToken cancellationToken) =>
{
    var user = await users.GetUserAsync(principal);
    if (user?.Email is null) return Results.Ok(Array.Empty<object>());
    var email = user.Email.ToUpperInvariant();
    var invitations = await database.ChallengeInvitations
        .Where(item => item.InviteeEmail != null
            && item.InviteeEmail.ToUpper() == email
            && item.RevokedAt == null
            && item.RedeemedAt == null
            && item.ExpiresAt > DateTimeOffset.UtcNow)
        .Join(database.Challenges, invitation => invitation.ChallengeId, challenge => challenge.Id,
            (invitation, challenge) => new { invitation, challenge })
        .Join(database.Users, pair => pair.invitation.InvitedByUserId, inviter => inviter.Id,
            (pair, inviter) => new
            {
                id = pair.invitation.Id,
                challengeName = pair.challenge.Name,
                inviterName = inviter.DisplayName,
                expiresAt = pair.invitation.ExpiresAt,
            })
        .ToListAsync(cancellationToken);
    return Results.Ok(invitations);
});
me.MapPost("/invitations/{invitationId:guid}/accept", async (
    Guid invitationId,
    ClaimsPrincipal principal,
    UserManager<ApplicationUser> users,
    ChallengeDbContext database,
    ScoreRecalculator recalculator,
    IHubContext<ChallengeHub> hub,
    CancellationToken cancellationToken) =>
{
    var user = await users.GetUserAsync(principal);
    if (user?.Email is null) return Results.Unauthorized();
    var invitation = await database.ChallengeInvitations
        .SingleOrDefaultAsync(item => item.Id == invitationId, cancellationToken);
    if (invitation is null || invitation.InviteeEmail?.Equals(user.Email, StringComparison.OrdinalIgnoreCase) != true)
        return Results.NotFound();

    var joinResult = await JoinChallengeAsync(invitation, user, database, recalculator, cancellationToken);
    if (joinResult.Error is not null) return joinResult.Error;
    await hub.Clients.Group($"challenge:{invitation.ChallengeId}")
        .SendAsync("challenge.updated", new { challengeId = invitation.ChallengeId }, cancellationToken);
    await hub.Clients.Group($"user:{invitation.InvitedByUserId}")
        .SendAsync("challenge.invitationReceived", new { challengeId = invitation.ChallengeId, accepted = true }, cancellationToken);
    return Results.Ok(new { challengeId = invitation.ChallengeId });
});

var challenges = app.MapGroup("/api/challenges").RequireAuthorization();
challenges.MapPost("/", async (
    ChallengeCreateRequest request,
    ClaimsPrincipal principal,
    UserManager<ApplicationUser> users,
    ChallengeDbContext database,
    CancellationToken cancellationToken) =>
{
    var normalizedName = request.Name?.Trim();
    if (string.IsNullOrWhiteSpace(normalizedName) || normalizedName.Length > 80)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Enter a challenge name between 1 and 80 characters."] });
    if (request.StartSeasonYear is < 1900 or > 2200)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["startSeasonYear"] = ["Choose a valid starting year."] });

    var user = await users.GetUserAsync(principal);
    if (user is null) return Results.Unauthorized();

    await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
    var challenge = new Challenge
    {
        Name = normalizedName,
        Description = request.Description?.Trim(),
        StartSeasonYear = request.StartSeasonYear,
        CreatedByUserId = user.Id,
    };
    var participant = new ChallengeParticipant
    {
        ChallengeId = challenge.Id,
        UserId = user.Id,
        Role = ParticipantRole.Administrator,
    };
    database.Challenges.Add(challenge);
    database.ChallengeParticipants.Add(participant);
    database.Careers.Add(new Career
    {
        ParticipantId = participant.Id,
        TrainerName = string.IsNullOrWhiteSpace(request.TrainerName) ? user.DisplayName : request.TrainerName.Trim(),
    });
    database.ChallengeRules.AddRange(AchievementCatalog.All.Select(definition => new ChallengeRule
    {
        ChallengeId = challenge.Id,
        AchievementCode = definition.Code,
        Points = definition.DefaultPoints,
        IsEnabled = definition.IsEnabled,
    }));
    database.ActivityEvents.Add(new ActivityEvent
    {
        ChallengeId = challenge.Id,
        ParticipantId = participant.Id,
        Type = "challenge.created",
        Message = $"{user.DisplayName} created {challenge.Name}.",
    });
    database.AuditLogs.Add(new AuditLog
    {
        ActorId = user.Id,
        ChallengeId = challenge.Id,
        EntityType = "Challenge",
        EntityId = challenge.Id.ToString(),
        Action = "Created",
        NewData = JsonSerializer.Serialize(new { challenge.Name, challenge.StartSeasonYear, challenge.TargetPoints }),
    });
    await database.SaveChangesAsync(cancellationToken);
    await transaction.CommitAsync(cancellationToken);
    return Results.Created($"/api/challenges/{challenge.Id}", new { id = challenge.Id });
});

challenges.MapPost("/{challengeId:guid}/invites", async (
    Guid challengeId,
    InviteCreateRequest? request,
    ClaimsPrincipal principal,
    UserManager<ApplicationUser> users,
    ChallengeDbContext database,
    IConfiguration configuration,
    IHubContext<ChallengeHub> hub,
    CancellationToken cancellationToken) =>
{
    var user = await users.GetUserAsync(principal);
    if (user is null) return Results.Unauthorized();
    var participant = await GetParticipantAsync(database, challengeId, user.Id, cancellationToken);
    if (participant is null || participant.Role != ParticipantRole.Administrator) return Results.Forbid();
    var challenge = await database.Challenges.SingleAsync(item => item.Id == challengeId, cancellationToken);
    var acceptedCount = await database.ChallengeParticipants.CountAsync(item => item.ChallengeId == challengeId, cancellationToken);
    if (acceptedCount >= 2 || challenge.Status is ChallengeStatus.Completed or ChallengeStatus.Archived)
        return Results.Conflict(new { title = "This challenge cannot accept another player." });

    var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    var invite = new ChallengeInvitation
    {
        ChallengeId = challengeId,
        InvitedByUserId = user.Id,
        TokenHash = HashToken(token),
        InviteeEmail = string.IsNullOrWhiteSpace(request?.Email) ? null : request.Email.Trim().ToUpperInvariant(),
        ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
    };
    database.ChallengeInvitations.Add(invite);
    if (invite.InviteeEmail is not null)
    {
        var invitedUser = await database.Users.SingleOrDefaultAsync(
            item => item.NormalizedEmail == invite.InviteeEmail, cancellationToken);
        if (invitedUser is not null)
        {
            database.UserNotifications.Add(new UserNotification
            {
                UserId = invitedUser.Id,
                ChallengeId = challengeId,
                Type = "challenge.invitation",
                Title = "A manager invited you",
                Message = $"{user.DisplayName} invited you to {challenge.Name}.",
                RelatedEntityId = invite.Id.ToString(),
            });
            await hub.Clients.Group($"user:{invitedUser.Id}")
                .SendAsync("challenge.invitationReceived", new { challengeId, invitationId = invite.Id }, cancellationToken);
        }
    }
    await database.SaveChangesAsync(cancellationToken);

    var baseUrl = configuration["Frontend:BaseUrl"] ?? "http://localhost:5173";
    var url = $"{baseUrl.TrimEnd('/')}/dashboard?invite={Uri.EscapeDataString(token)}";
    return Results.Ok(new { id = invite.Id, url, expiresAt = invite.ExpiresAt });
});

challenges.MapPost("/join", async (
    JoinChallengeRequest request,
    ClaimsPrincipal principal,
    UserManager<ApplicationUser> users,
    ChallengeDbContext database,
    ScoreRecalculator recalculator,
    IHubContext<ChallengeHub> hub,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Token)) return Results.BadRequest(new { title = "An invitation token is required." });
    var user = await users.GetUserAsync(principal);
    if (user is null) return Results.Unauthorized();
    var hash = HashToken(request.Token.Trim());
    var invitation = await database.ChallengeInvitations.SingleOrDefaultAsync(
        item => item.TokenHash == hash, cancellationToken);
    if (invitation is null) return Results.NotFound(new { title = "This invitation is invalid." });
    var result = await JoinChallengeAsync(invitation, user, database, recalculator, cancellationToken);
    if (result.Error is not null) return result.Error;
    await hub.Clients.Group($"challenge:{invitation.ChallengeId}")
        .SendAsync("challenge.updated", new { challengeId = invitation.ChallengeId }, cancellationToken);
    return Results.Ok(new { challengeId = invitation.ChallengeId });
});

challenges.MapGet("/{challengeId:guid}/dashboard", async (
    Guid challengeId,
    ClaimsPrincipal principal,
    ChallengeDbContext database,
    CancellationToken cancellationToken) =>
{
    var userId = GetUserId(principal);
    var currentParticipant = await GetParticipantAsync(database, challengeId, userId, cancellationToken);
    if (currentParticipant is null) return Results.Forbid();
    var challenge = await database.Challenges.SingleAsync(item => item.Id == challengeId, cancellationToken);
    var participantData = await (
        from participant in database.ChallengeParticipants
        join user in database.Users on participant.UserId equals user.Id
        join career in database.Careers on participant.Id equals career.ParticipantId into careers
        from career in careers.DefaultIfEmpty()
        where participant.ChallengeId == challengeId
        select new { participant.Id, participant.UserId, Role = participant.Role.ToString(), user.DisplayName, user.AvatarUrl, TrainerName = career == null ? null : career.TrainerName })
        .ToListAsync(cancellationToken);
    var ids = participantData.Select(item => item.Id).ToArray();
    var scores = await database.ScoreEvents
        .Where(item => item.ChallengeId == challengeId && ids.Contains(item.ParticipantId) && item.Status == "Confirmed")
        .GroupBy(item => item.ParticipantId)
        .Select(group => new { ParticipantId = group.Key, Points = group.Sum(item => item.Points) })
        .ToDictionaryAsync(item => item.ParticipantId, item => item.Points, cancellationToken);
    return Results.Ok(new
    {
        challenge = new
        {
            challenge.Id,
            challenge.Name,
            challenge.Description,
            Mode = challenge.Mode,
            challenge.TargetPoints,
            challenge.StartSeasonYear,
            Status = challenge.Status.ToString(),
            challenge.WinningParticipantId,
            challenge.WinningSeasonIndex,
        },
        participants = participantData.Select(item => new
        {
            item.Id,
            item.UserId,
            item.DisplayName,
            item.AvatarUrl,
            item.TrainerName,
            item.Role,
            score = scores.GetValueOrDefault(item.Id),
            isCurrentUser = item.Id == currentParticipant.Id,
        }),
    });
});

challenges.MapGet("/{challengeId:guid}/scores", async (
    Guid challengeId,
    ClaimsPrincipal principal,
    ChallengeDbContext database,
    CancellationToken cancellationToken) =>
{
    var currentParticipant = await GetParticipantAsync(
        database, challengeId, GetUserId(principal), cancellationToken);
    if (currentParticipant is null) return Results.Forbid();

    var participants = await (
        from participant in database.ChallengeParticipants
        join user in database.Users on participant.UserId equals user.Id
        join career in database.Careers on participant.Id equals career.ParticipantId into careers
        from career in careers.DefaultIfEmpty()
        where participant.ChallengeId == challengeId
        select new
        {
            participant.Id,
            Name = career == null ? user.DisplayName : career.TrainerName,
            IsCurrentUser = participant.Id == currentParticipant.Id,
        }).ToListAsync(cancellationToken);
    var participantIds = participants.Select(item => item.Id).ToArray();
    var events = await database.ScoreEvents
        .Where(item => item.ChallengeId == challengeId && participantIds.Contains(item.ParticipantId))
        .Select(item => new { item.ParticipantId, item.SeasonIndex, item.Points, item.Status })
        .ToListAsync(cancellationToken);
    var careerIds = await database.Careers
        .Where(item => participantIds.Contains(item.ParticipantId))
        .Select(item => item.Id)
        .ToArrayAsync(cancellationToken);
    var recordedSeasonIndices = await database.CareerSeasons
        .Where(item => careerIds.Contains(item.CareerId))
        .Select(item => item.SeasonIndex)
        .Distinct()
        .ToListAsync(cancellationToken);
    var allIndices = events.Select(item => item.SeasonIndex)
        .Concat(recordedSeasonIndices)
        .Distinct()
        .Order()
        .ToArray();

    var series = new List<object>();
    foreach (var participant in participants)
    {
        var cumulative = 0;
        foreach (var seasonIndex in allIndices)
        {
            var seasonEvents = events.Where(item => item.ParticipantId == participant.Id && item.SeasonIndex == seasonIndex).ToArray();
            var confirmed = seasonEvents.Where(item => item.Status == "Confirmed").Sum(item => item.Points);
            var potential = seasonEvents.Sum(item => item.Points);
            cumulative += confirmed;
            series.Add(new
            {
                participantId = participant.Id,
                participantName = participant.Name,
                isCurrentUser = participant.IsCurrentUser,
                seasonIndex,
                confirmedPoints = confirmed,
                potentialPoints = potential,
                cumulativePoints = cumulative,
            });
        }
    }

    return Results.Ok(new { participants, series });
});

challenges.MapGet("/{challengeId:guid}/achievements", async (
    Guid challengeId,
    ClaimsPrincipal principal,
    ChallengeDbContext database,
    CancellationToken cancellationToken) =>
{
    var userId = GetUserId(principal);
    var participant = await GetParticipantAsync(database, challengeId, userId, cancellationToken);
    if (participant is null) return Results.Forbid();
    var rules = await database.ChallengeRules.Where(item => item.ChallengeId == challengeId && item.VersionNumber == 1)
        .ToDictionaryAsync(item => item.AchievementCode, cancellationToken);
    var claims = await database.AchievementClaims.Where(item => item.ParticipantId == participant.Id)
        .OrderByDescending(item => item.SeasonIndex)
        .ToListAsync(cancellationToken);
    return Results.Ok(AchievementCatalog.All.Select(definition => new
    {
        definition.Code,
        definition.Name,
        definition.Description,
        definition.Category,
        Frequency = definition.Frequency.ToString(),
        Points = rules.GetValueOrDefault(definition.Code)?.Points ?? definition.DefaultPoints,
        IsEnabled = rules.GetValueOrDefault(definition.Code)?.IsEnabled ?? definition.IsEnabled,
        Claims = claims.Where(item => item.AchievementCode == definition.Code)
            .Select(item => new { item.Id, item.SeasonIndex, item.ClubName, item.CountryCode, item.Notes }),
    }));
});

challenges.MapGet("/{challengeId:guid}/career", async (
    Guid challengeId,
    ClaimsPrincipal principal,
    ChallengeDbContext database,
    CancellationToken cancellationToken) =>
{
    var participant = await GetParticipantAsync(database, challengeId, GetUserId(principal), cancellationToken);
    if (participant is null) return Results.Forbid();
    var career = await database.Careers.SingleAsync(item => item.ParticipantId == participant.Id, cancellationToken);
    var stints = await database.CareerStints.Where(item => item.CareerId == career.Id)
        .OrderBy(item => item.StartSeasonIndex)
        .ThenBy(item => item.ClubName)
        .ToListAsync(cancellationToken);
    return Results.Ok(new
    {
        career.Id,
        career.TrainerName,
        career.CurrentClubName,
        career.CurrentCountryCode,
        clubs = stints.Select(item => new
        {
            item.Id,
            item.ClubName,
            item.CountryCode,
            item.CountryName,
            item.StartSeasonIndex,
            item.EndSeasonIndex,
            item.InitialDivisionLevel,
            item.OfficialMatches,
        }),
    });
});
challenges.MapPost("/{challengeId:guid}/career/stints", async (
    Guid challengeId,
    CareerStintCreateRequest request,
    ClaimsPrincipal principal,
    ChallengeDbContext database,
    ScoreRecalculator recalculator,
    IHubContext<ChallengeHub> hub,
    CancellationToken cancellationToken) =>
{
    var userId = GetUserId(principal);
    var participant = await GetParticipantAsync(database, challengeId, userId, cancellationToken);
    if (participant is null) return Results.Forbid();
    var challenge = await database.Challenges.SingleAsync(item => item.Id == challengeId, cancellationToken);
    if (challenge.Status is ChallengeStatus.Completed or ChallengeStatus.Archived)
        return Results.Conflict(new { title = "This challenge is read-only." });
    if (string.IsNullOrWhiteSpace(request.ClubName) || request.ClubName.Trim().Length > 120
        || string.IsNullOrWhiteSpace(request.CountryCode) || request.CountryCode.Trim().Length > 8
        || string.IsNullOrWhiteSpace(request.CountryName) || request.CountryName.Trim().Length > 80
        || request.StartSeasonIndex is < 0 or > 100
        || request.InitialDivisionLevel < 1
        || request.OfficialMatches < 0)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["career"] = ["Enter a club, country, season, division level, and non-negative match count."] });

    var career = await database.Careers.SingleAsync(item => item.ParticipantId == participant.Id, cancellationToken);
    var stint = new CareerStint
    {
        CareerId = career.Id,
        ClubName = request.ClubName.Trim(),
        CountryCode = request.CountryCode.Trim().ToUpperInvariant(),
        CountryName = request.CountryName.Trim(),
        StartSeasonIndex = request.StartSeasonIndex,
        EndSeasonIndex = request.EndSeasonIndex,
        InitialDivisionLevel = request.InitialDivisionLevel,
        OfficialMatches = request.OfficialMatches,
    };
    database.CareerStints.Add(stint);
    if (!request.EndSeasonIndex.HasValue)
    {
        career.CurrentClubName = stint.ClubName;
        career.CurrentCountryCode = stint.CountryCode;
    }

    database.ActivityEvents.Add(new ActivityEvent
    {
        ChallengeId = challengeId,
        ParticipantId = participant.Id,
        Type = "career.clubChanged",
        Message = $"{career.TrainerName} joined {stint.ClubName}.",
        ClubName = stint.ClubName,
        SeasonIndex = stint.StartSeasonIndex,
    });
    database.AuditLogs.Add(new AuditLog
    {
        ActorId = userId,
        ChallengeId = challengeId,
        EntityType = "CareerStint",
        EntityId = stint.Id.ToString(),
        Action = "Created",
        NewData = JsonSerializer.Serialize(new { stint.ClubName, stint.CountryCode, stint.StartSeasonIndex }),
    });
    await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
    await database.SaveChangesAsync(cancellationToken);
    var score = await recalculator.RecalculateAsync(challengeId, cancellationToken);
    await transaction.CommitAsync(cancellationToken);
    await hub.Clients.Group($"challenge:{challengeId}")
        .SendAsync("career.clubChanged", new { challengeId, clubName = stint.ClubName, seasonIndex = stint.StartSeasonIndex }, cancellationToken);
    await hub.Clients.Group($"challenge:{challengeId}")
        .SendAsync("score.updated", new { challengeId, scores = score.Participants }, cancellationToken);
    await hub.Clients.Group($"challenge:{challengeId}")
        .SendAsync("challenge.updated", new { challengeId }, cancellationToken);
    return Results.Created($"/api/challenges/{challengeId}/career/stints/{stint.Id}", new { stint.Id });
});

challenges.MapPost("/{challengeId:guid}/achievement-claims", async (
    Guid challengeId,
    ClaimCreateRequest request,
    ClaimsPrincipal principal,
    ChallengeDbContext database,
    ScoreRecalculator recalculator,
    IHubContext<ChallengeHub> hub,
    CancellationToken cancellationToken) =>
{
    var userId = GetUserId(principal);
    var participant = await GetParticipantAsync(database, challengeId, userId, cancellationToken);
    if (participant is null) return Results.Forbid();
    var challenge = await database.Challenges.SingleAsync(item => item.Id == challengeId, cancellationToken);
    if (challenge.Status is ChallengeStatus.Completed or ChallengeStatus.Archived)
        return Results.Conflict(new { title = "This challenge is read-only." });
    var definition = AchievementCatalog.All.SingleOrDefault(item => item.Code == request.Code);
    if (definition is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["code"] = ["Choose a valid achievement."] });
    var rule = await database.ChallengeRules.SingleOrDefaultAsync(
        item => item.ChallengeId == challengeId && item.AchievementCode == request.Code && item.VersionNumber == 1,
        cancellationToken);
    if (rule is null || !rule.IsEnabled) return Results.Conflict(new { title = "This achievement is disabled for this challenge." });
    if (request.SeasonIndex < 0 || request.SeasonIndex > 100)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["seasonIndex"] = ["Choose a season index between 0 and 100."] });

    var career = await database.Careers.SingleAsync(item => item.ParticipantId == participant.Id, cancellationToken);
    var existingClaims = await database.AchievementClaims
        .Where(item => item.ParticipantId == participant.Id && item.AchievementCode == request.Code)
        .ToListAsync(cancellationToken);
    var duplicate = definition.Frequency switch
    {
        AchievementFrequency.Once => existingClaims.Count > 0,
        AchievementFrequency.Season => existingClaims.Any(item => item.SeasonIndex == request.SeasonIndex),
        AchievementFrequency.PerClub => existingClaims.Any(item => Same(item.ClubName, request.ClubName)),
        AchievementFrequency.PerCountry => existingClaims.Any(item => Same(item.CountryCode, request.CountryCode)),
        AchievementFrequency.ClubSeason => existingClaims.Any(item => item.SeasonIndex == request.SeasonIndex && Same(item.ClubName, request.ClubName)),
        _ => true,
    };
    if (duplicate) return Results.Conflict(new { title = "This achievement has already been recorded at its allowed frequency." });
    if (definition.Frequency is AchievementFrequency.PerClub or AchievementFrequency.ClubSeason && string.IsNullOrWhiteSpace(request.ClubName))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["clubName"] = ["A club is required for this achievement."] });
    if (definition.Frequency == AchievementFrequency.PerCountry && string.IsNullOrWhiteSpace(request.CountryCode))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["countryCode"] = ["A country is required for this achievement."] });

    var claim = new AchievementClaim
    {
        CareerId = career.Id,
        ParticipantId = participant.Id,
        AchievementCode = request.Code,
        OccurrenceKey = GetOccurrenceKey(definition.Frequency, request.SeasonIndex, request.ClubName, request.CountryCode),
        SeasonIndex = request.SeasonIndex,
        ClubName = request.ClubName?.Trim(),
        CountryCode = request.CountryCode?.Trim().ToUpperInvariant(),
        Notes = request.Notes?.Trim(),
    };
    database.AchievementClaims.Add(claim);
    var activity = new ActivityEvent
    {
        ChallengeId = challengeId,
        ParticipantId = participant.Id,
        Type = "achievement.created",
        Message = $"{definition.Name} recorded for season {challenge.StartSeasonYear + request.SeasonIndex}/{challenge.StartSeasonYear + request.SeasonIndex + 1}.",
        ClubName = request.ClubName?.Trim(),
        SeasonIndex = request.SeasonIndex,
    };
    database.ActivityEvents.Add(activity);
    database.AuditLogs.Add(new AuditLog
    {
        ActorId = userId,
        ChallengeId = challengeId,
        EntityType = "AchievementClaim",
        EntityId = claim.Id.ToString(),
        Action = "Created",
        NewData = JsonSerializer.Serialize(new { claim.AchievementCode, claim.SeasonIndex, claim.ClubName, claim.CountryCode }),
    });
    await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
    await database.SaveChangesAsync(cancellationToken);
    var score = await recalculator.RecalculateAsync(challengeId, cancellationToken);
    await transaction.CommitAsync(cancellationToken);
    await hub.Clients.Group($"challenge:{challengeId}")
        .SendAsync("achievement.created", new { challengeId, claim.Id, code = claim.AchievementCode, seasonIndex = claim.SeasonIndex }, cancellationToken);
    await hub.Clients.Group($"challenge:{challengeId}")
        .SendAsync("score.updated", new { challengeId, scores = score.Participants }, cancellationToken);
    await hub.Clients.Group($"challenge:{challengeId}")
        .SendAsync("challenge.updated", new { challengeId }, cancellationToken);
    return Results.Created($"/api/challenges/{challengeId}/achievement-claims/{claim.Id}", new
    {
        claim.Id,
        claim.AchievementCode,
        claim.SeasonIndex,
        points = rule.Points,
    });
});

challenges.MapGet("/{challengeId:guid}/seasons", async (
    Guid challengeId,
    ClaimsPrincipal principal,
    ChallengeDbContext database,
    CancellationToken cancellationToken) =>
{
    var participant = await GetParticipantAsync(database, challengeId, GetUserId(principal), cancellationToken);
    if (participant is null) return Results.Forbid();
    var career = await database.Careers.SingleAsync(item => item.ParticipantId == participant.Id, cancellationToken);
    var seasons = await database.CareerSeasons.Where(item => item.CareerId == career.Id)
        .OrderBy(item => item.SeasonIndex).ToListAsync(cancellationToken);
    return Results.Ok(seasons.Select(item => new
    {
        item.Id,
        item.SeasonIndex,
        item.DisplayName,
        Status = item.Status.ToString(),
        item.LeagueMatches,
        item.LeagueWins,
        item.LeagueDraws,
        item.LeagueLosses,
        item.LeaguePoints,
        item.GoalsFor,
        item.GoalsAgainst,
        item.LeaguePosition,
        item.LargestOutgoingTransfer,
        item.Currency,
        item.Notes,
    }));
});
challenges.MapPut("/{challengeId:guid}/seasons/{seasonIndex:int}", async (
    Guid challengeId,
    int seasonIndex,
    SeasonUpdateRequest request,
    ClaimsPrincipal principal,
    ChallengeDbContext database,
    ScoreRecalculator recalculator,
    IHubContext<ChallengeHub> hub,
    CancellationToken cancellationToken) =>
{
    var participant = await GetParticipantAsync(database, challengeId, GetUserId(principal), cancellationToken);
    if (participant is null) return Results.Forbid();
    var challenge = await database.Challenges.SingleAsync(item => item.Id == challengeId, cancellationToken);
    if (challenge.Status is ChallengeStatus.Completed or ChallengeStatus.Archived)
        return Results.Conflict(new { title = "This challenge is read-only." });
    if (seasonIndex is < 0 or > 100 || request.LeagueMatches < 0
        || request.LeagueWins < 0 || request.LeagueDraws < 0 || request.LeagueLosses < 0
        || request.LeaguePoints < 0 || request.GoalsFor < 0 || request.GoalsAgainst < 0
        || request.LargestOutgoingTransfer < 0)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["season"] = ["Season statistics must be non-negative and use a valid season index."] });

    var career = await database.Careers.SingleAsync(item => item.ParticipantId == participant.Id, cancellationToken);
    var season = await database.CareerSeasons.SingleOrDefaultAsync(
        item => item.CareerId == career.Id && item.SeasonIndex == seasonIndex, cancellationToken);
    var wasCompleted = season?.Status == SeasonStatus.Completed;
    if (season is null)
    {
        season = new CareerSeason
        {
            CareerId = career.Id,
            SeasonIndex = seasonIndex,
            StartYear = challenge.StartSeasonYear + seasonIndex,
            DisplayName = $"{challenge.StartSeasonYear + seasonIndex}/{(challenge.StartSeasonYear + seasonIndex + 1) % 100:00}",
        };
        database.CareerSeasons.Add(season);
    }

    season.LeagueMatches = request.LeagueMatches;
    season.LeagueWins = request.LeagueWins;
    season.LeagueDraws = request.LeagueDraws;
    season.LeagueLosses = request.LeagueLosses;
    season.LeaguePoints = request.LeaguePoints;
    season.GoalsFor = request.GoalsFor;
    season.GoalsAgainst = request.GoalsAgainst;
    season.LeaguePosition = request.LeaguePosition;
    season.LargestOutgoingTransfer = request.LargestOutgoingTransfer;
    season.Currency = string.IsNullOrWhiteSpace(request.Currency) ? "EUR" : request.Currency.Trim().ToUpperInvariant();
    season.Notes = request.Notes?.Trim();
    season.Status = request.IsCompleted ? SeasonStatus.Completed : SeasonStatus.InProgress;
    season.CompletedAt = request.IsCompleted ? DateTimeOffset.UtcNow : null;
    season.Version++;
    await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
    await database.SaveChangesAsync(cancellationToken);
    var score = await recalculator.RecalculateAsync(challengeId, cancellationToken);
    await transaction.CommitAsync(cancellationToken);
    await hub.Clients.Group($"challenge:{challengeId}")
        .SendAsync(
            request.IsCompleted ? "season.completed" : wasCompleted ? "season.reopened" : "season.updated",
            new { challengeId, seasonIndex },
            cancellationToken);
    await hub.Clients.Group($"challenge:{challengeId}")
        .SendAsync("score.updated", new { challengeId, scores = score.Participants }, cancellationToken);
    await hub.Clients.Group($"challenge:{challengeId}")
        .SendAsync("challenge.updated", new { challengeId }, cancellationToken);
    return Results.Ok(new { season.Id, season.SeasonIndex, Status = season.Status.ToString() });
});

challenges.MapGet("/{challengeId:guid}/activity", async (
    Guid challengeId,
    ClaimsPrincipal principal,
    ChallengeDbContext database,
    CancellationToken cancellationToken) =>
{
    if (await GetParticipantAsync(database, challengeId, GetUserId(principal), cancellationToken) is null)
        return Results.Forbid();
    var events = await (
        from activity in database.ActivityEvents
        join participant in database.ChallengeParticipants on activity.ParticipantId equals participant.Id
        join user in database.Users on participant.UserId equals user.Id
        where activity.ChallengeId == challengeId
        orderby activity.CreatedAt descending
        select new
        {
            activity.Id,
            activity.Type,
            activity.Message,
            activity.ClubName,
            activity.SeasonIndex,
            activity.Points,
            activity.CreatedAt,
            participantName = user.DisplayName,
        }).Take(50).ToListAsync(cancellationToken);
    return Results.Ok(events);
});

var userNotifications = app.MapGroup("/api/me/notifications").RequireAuthorization();
userNotifications.MapGet("/", async (ClaimsPrincipal principal, ChallengeDbContext database, CancellationToken cancellationToken) =>
{
    var userId = GetUserId(principal);
    var items = await database.UserNotifications.Where(item => item.UserId == userId)
        .OrderByDescending(item => item.CreatedAt).Take(100).ToListAsync(cancellationToken);
    return Results.Ok(items);
});
userNotifications.MapPatch("/{notificationId:guid}/read", async (
    Guid notificationId,
    ClaimsPrincipal principal,
    ChallengeDbContext database,
    CancellationToken cancellationToken) =>
{
    var userId = GetUserId(principal);
    var notification = await database.UserNotifications.SingleOrDefaultAsync(
        item => item.Id == notificationId && item.UserId == userId, cancellationToken);
    if (notification is null) return Results.NotFound();
    notification.ReadAt = DateTimeOffset.UtcNow;
    await database.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
});

app.MapHub<ChallengeHub>("/hubs/challenges").RequireAuthorization();

if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
    await database.Database.MigrateAsync();
}

app.Run();

static Guid GetUserId(ClaimsPrincipal principal) =>
    Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
        ? userId
        : throw new UnauthorizedAccessException("The authenticated user identifier is invalid.");

static Task<ChallengeParticipant?> GetParticipantAsync(
    ChallengeDbContext database,
    Guid challengeId,
    Guid userId,
    CancellationToken cancellationToken) =>
    database.ChallengeParticipants.SingleOrDefaultAsync(
        item => item.ChallengeId == challengeId && item.UserId == userId, cancellationToken);

static async Task<(IResult? Error, Guid? ParticipantId)> JoinChallengeAsync(
    ChallengeInvitation invitation,
    ApplicationUser user,
    ChallengeDbContext database,
    ScoreRecalculator recalculator,
    CancellationToken cancellationToken)
{
    if (invitation.RevokedAt.HasValue || invitation.RedeemedAt.HasValue || invitation.ExpiresAt <= DateTimeOffset.UtcNow)
        return (Results.Conflict(new { title = "This invitation has expired or has already been used." }), null);
    if (invitation.InviteeEmail is not null && !invitation.InviteeEmail.Equals(user.Email, StringComparison.OrdinalIgnoreCase))
        return (Results.Forbid(), null);

    var challenge = await database.Challenges.SingleOrDefaultAsync(
        item => item.Id == invitation.ChallengeId, cancellationToken);
    if (challenge is null || challenge.Status is ChallengeStatus.Completed or ChallengeStatus.Archived)
        return (Results.Conflict(new { title = "This challenge is not accepting players." }), null);
    if (await database.ChallengeParticipants.AnyAsync(
            item => item.ChallengeId == challenge.Id && item.UserId == user.Id, cancellationToken))
        return (Results.Conflict(new { title = "You are already a participant in this challenge." }), null);
    if (await database.ChallengeParticipants.CountAsync(item => item.ChallengeId == challenge.Id, cancellationToken) >= 2)
        return (Results.Conflict(new { title = "This challenge already has two participants." }), null);

    await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
    var participant = new ChallengeParticipant
    {
        ChallengeId = challenge.Id,
        UserId = user.Id,
        Role = ParticipantRole.Participant,
    };
    database.ChallengeParticipants.Add(participant);
    database.Careers.Add(new Career
    {
        ParticipantId = participant.Id,
        TrainerName = user.DisplayName,
    });
    invitation.RedeemedAt = DateTimeOffset.UtcNow;
    challenge.Status = ChallengeStatus.Active;
    database.ActivityEvents.Add(new ActivityEvent
    {
        ChallengeId = challenge.Id,
        ParticipantId = participant.Id,
        Type = "challenge.playerJoined",
        Message = $"{user.DisplayName} joined {challenge.Name}.",
    });
    database.UserNotifications.Add(new UserNotification
    {
        UserId = challenge.CreatedByUserId,
        ChallengeId = challenge.Id,
        Type = "challenge.invitation.accepted",
        Title = "Your invitation was accepted",
        Message = $"{user.DisplayName} joined {challenge.Name}.",
        RelatedEntityId = invitation.Id.ToString(),
    });
    database.AuditLogs.Add(new AuditLog
    {
        ActorId = user.Id,
        ChallengeId = challenge.Id,
        EntityType = "ChallengeInvitation",
        EntityId = invitation.Id.ToString(),
        Action = "Accepted",
    });
    await database.SaveChangesAsync(cancellationToken);
    await recalculator.RecalculateAsync(challenge.Id, cancellationToken);
    await transaction.CommitAsync(cancellationToken);
    return (null, participant.Id);
}

static string HashToken(string token) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

static string GetOccurrenceKey(
    AchievementFrequency frequency,
    int seasonIndex,
    string? clubName,
    string? countryCode) =>
    frequency switch
    {
        AchievementFrequency.Once => "once",
        AchievementFrequency.Season => $"season:{seasonIndex}",
        AchievementFrequency.PerClub => $"club:{clubName?.Trim().ToUpperInvariant()}",
        AchievementFrequency.PerCountry => $"country:{countryCode?.Trim().ToUpperInvariant()}",
        AchievementFrequency.ClubSeason => $"club-season:{clubName?.Trim().ToUpperInvariant()}:{seasonIndex}",
        _ => throw new ArgumentOutOfRangeException(nameof(frequency)),
    };

static bool Same(string? left, string? right) =>
    !string.IsNullOrWhiteSpace(left)
    && !string.IsNullOrWhiteSpace(right)
    && left.Trim().Equals(right.Trim(), StringComparison.OrdinalIgnoreCase);

public sealed record ChallengeCreateRequest(string? Name, string? Description, int StartSeasonYear = 2026, string? TrainerName = null);
public sealed record InviteCreateRequest(string? Email);
public sealed record JoinChallengeRequest(string? Token);
public sealed record ClaimCreateRequest(string Code, int SeasonIndex, string? ClubName, string? CountryCode, string? Notes);
public sealed record CareerStintCreateRequest(
    string ClubName,
    string CountryCode,
    string CountryName,
    int StartSeasonIndex,
    int? EndSeasonIndex,
    int InitialDivisionLevel,
    int OfficialMatches);
public sealed record SeasonUpdateRequest(
    int LeagueMatches,
    int LeagueWins,
    int LeagueDraws,
    int LeagueLosses,
    int LeaguePoints,
    int GoalsFor,
    int GoalsAgainst,
    int? LeaguePosition,
    decimal LargestOutgoingTransfer,
    string? Currency,
    string? Notes,
    bool IsCompleted);

public partial class Program;
