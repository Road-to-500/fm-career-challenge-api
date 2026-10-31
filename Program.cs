using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Data;
using System.Net;
using FmCareerChallenge.Api.Domain;
using FmCareerChallenge.Api.Infrastructure;
using FmCareerChallenge.Api.Realtime;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
if (args.Contains("--migrate", StringComparer.Ordinal))
{
    var migrationConnection = builder.Configuration.GetConnectionString("PostgreSql")
        ?? throw new InvalidOperationException("Migrations require ConnectionStrings:PostgreSql.");
    var migrationHost = new Npgsql.NpgsqlConnectionStringBuilder(migrationConnection).Host;
    if (string.IsNullOrWhiteSpace(migrationHost))
        throw new InvalidOperationException("Migrations require an explicit PostgreSQL host.");
    if (migrationHost.Contains("-pooler", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Migrations require a direct PostgreSQL connection, not a pooled endpoint.");
    var migrationOptions = new DbContextOptionsBuilder<ChallengeDbContext>()
        .UseNpgsql(migrationConnection)
        .UseLoggerFactory(LoggerFactory.Create(logging => logging.AddConsole()))
        .Options;
    await using var migrationDatabase = new ChallengeDbContext(migrationOptions);
    await DatabaseMigrations.ApplyAsync(migrationDatabase);
    Console.WriteLine("Database migrations completed.");
    return;
}
var connectionString = builder.Configuration.GetConnectionString("PostgreSql")
    ?? "Host=localhost;Port=5432;Database=fm_career_challenge;Username=postgres;Password=postgres";
var frontendOrigin = builder.Configuration["Frontend:Origin"] ?? "http://localhost:5173";
ProductionSecurity.Configure(builder.Services, builder.Configuration, builder.Environment.IsDevelopment());
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    var trustedProxies = builder.Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [];
    options.ForwardedHeaders = trustedProxies.Length == 0
        ? ForwardedHeaders.None
        : ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.KnownProxies.Clear();
    options.KnownIPNetworks.Clear();
    foreach (var proxy in trustedProxies)
        options.KnownProxies.Add(IPAddress.Parse(proxy));
});

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
    {
        var userId = context.User.Identity?.IsAuthenticated == true
            ? context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            : null;
        var partition = userId is null
            ? $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}"
            : $"user:{userId}";
        var permitLimit = userId is null ? 120 : 300;
        return RateLimitPartition.GetFixedWindowLimiter(
            partition,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            });
    });
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
        options.ClaimActions.MapJsonKey("email_verified", "email_verified", "bool");
        options.SaveTokens = false;
    });
}

builder.Services.AddAuthorization();

var app = builder.Build();

app.UseExceptionHandler();
app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseCors();
app.UseAuthentication();
app.UseRateLimiter();
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
app.Use(async (context, next) =>
{
    var method = context.Request.Method;
    var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? string.Empty;
    var requiresStart = method is not ("GET" or "HEAD" or "OPTIONS" or "TRACE")
        && (route.Contains("/achievement-claims", StringComparison.Ordinal)
            || route.Contains("/national-teams", StringComparison.Ordinal)
            || route.Contains("/transfers", StringComparison.Ordinal)
            || route.Contains("/seasons/", StringComparison.Ordinal)
            || route.EndsWith("/career/three-continents-milestone", StringComparison.Ordinal)
            || route.EndsWith("/career/national-team-milestone", StringComparison.Ordinal)
            || route.EndsWith("/country-milestone", StringComparison.Ordinal)
            || route.EndsWith("/matches", StringComparison.Ordinal));
    if (requiresStart && context.User.Identity?.IsAuthenticated == true
        && Guid.TryParse(context.Request.RouteValues["challengeId"]?.ToString(), out var challengeId)
        && Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
    {
        var database = context.RequestServices.GetRequiredService<ChallengeDbContext>();
        var pending = await (from participant in database.ChallengeParticipants
                             join career in database.Careers on participant.Id equals career.ParticipantId
                             where participant.ChallengeId == challengeId && participant.UserId == userId
                                 && career.RequiresStartingStatus && career.StartingStatus == null
                             select career.Id).AnyAsync(context.RequestAborted);
        if (pending)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(new { title = "Record your career start before entering gameplay results.", status = 409 });
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
auth.MapGet("/google", (IConfiguration configuration, SignInManager<ApplicationUser> signInManager) =>
{
    if (string.IsNullOrWhiteSpace(configuration["Authentication:Google:ClientId"])
        || string.IsNullOrWhiteSpace(configuration["Authentication:Google:ClientSecret"]))
    {
        return Results.Problem(
            "Google sign-in is not configured. Set Authentication__Google__ClientId and Authentication__Google__ClientSecret.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Challenge(
        signInManager.ConfigureExternalAuthenticationProperties(
            GoogleDefaults.AuthenticationScheme, "/api/auth/external-callback"),
        [GoogleDefaults.AuthenticationScheme]);
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

        if (!bool.TryParse(externalInfo.Principal.FindFirstValue("email_verified"), out var emailVerified)
            || !emailVerified)
        {
            await signInManager.SignOutAsync();
            return Results.Problem("Google must verify the email address before an account can be created.", statusCode: StatusCodes.Status403Forbidden);
        }

        if (await userManager.FindByEmailAsync(email) is not null)
        {
            await signInManager.SignOutAsync();
            return Results.Problem(
                "An account already uses this email with another sign-in provider. Sign in with that provider before linking accounts.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var displayName = externalInfo.Principal.FindFirstValue(ClaimTypes.Name);
        var publicName = string.IsNullOrWhiteSpace(displayName) ? email.Split('@')[0] : displayName;
        user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = publicName[..Math.Min(80, publicName.Length)],
            AvatarUrl = externalInfo.Principal.FindFirstValue("picture") is { Length: <= 2048 } picture ? picture : null,
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
        : Results.Ok(new { id = user.Id, displayName = user.DisplayName, email = user.Email, avatarUrl = user.AvatarUrl, user.HasCompletedOnboarding });
});
me.MapPut("/", async (
    ProfileUpdateRequest request, ClaimsPrincipal principal, UserManager<ApplicationUser> users,
    ChallengeDbContext database, IHubContext<ChallengeHub> hub, CancellationToken cancellationToken) =>
{
    var user = await users.GetUserAsync(principal);
    if (user is null) return Results.Unauthorized();
    var name = request.DisplayName?.Trim();
    var avatar = string.IsNullOrWhiteSpace(request.AvatarUrl) ? null : request.AvatarUrl.Trim();
    if (string.IsNullOrWhiteSpace(name) || name.Length > 80)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["displayName"] = ["Enter a public name between 1 and 80 characters."] });
    if (avatar is not null && (avatar.Length > 2048 || !Uri.TryCreate(avatar, UriKind.Absolute, out var avatarUri)
        || avatarUri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(avatarUri.UserInfo)))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["avatarUrl"] = ["Enter a valid HTTPS avatar URL or leave it empty."] });
    user.DisplayName = name;
    user.AvatarUrl = avatar;
    user.HasCompletedOnboarding = true;
    var result = await users.UpdateAsync(user);
    if (!result.Succeeded)
    {
        if (result.Errors.Any(error => error.Code == "ConcurrencyFailure"))
            return Results.Conflict(new { title = "Your profile changed. Refresh and try again." });
        return Results.Problem(string.Join("; ", result.Errors.Select(error => error.Description)), statusCode: 500);
    }
    var challengeIds = await database.ChallengeParticipants.Where(item => item.UserId == user.Id)
        .Select(item => item.ChallengeId).ToArrayAsync(cancellationToken);
    foreach (var challengeId in challengeIds)
        await hub.Clients.Group($"challenge:{challengeId}").SendAsync("challenge.updated", new { challengeId }, cancellationToken);
    await hub.Clients.Group($"user:{user.Id}").SendAsync("notification.created", new { profileUpdated = true }, cancellationToken);
    return Results.Ok(new { id = user.Id, displayName = user.DisplayName, email = user.Email, avatarUrl = user.AvatarUrl, user.HasCompletedOnboarding });
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
            challenge.StatusBeforeArchive,
            Role = participant.Role.ToString(),
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
    var ownParticipantIds = results.Select(item => item.ParticipantId).ToArray();
    var latestSeasons = await (
        from season in database.CareerSeasons
        join career in database.Careers on season.CareerId equals career.Id
        where ownParticipantIds.Contains(career.ParticipantId)
        select new { career.ParticipantId, season.SeasonIndex, season.DisplayName }
    ).ToListAsync(cancellationToken);
    var latestSeasonByParticipant = latestSeasons.GroupBy(item => item.ParticipantId)
        .ToDictionary(group => group.Key, group => group.MaxBy(item => item.SeasonIndex)!.DisplayName);

    var response = results.Select(item => new
    {
        id = item.ChallengeId,
        name = item.ChallengeName,
        status = item.Status,
        statusBeforeArchive = item.StatusBeforeArchive?.ToString(),
        role = item.Role,
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
        currentSeason = latestSeasonByParticipant.GetValueOrDefault(item.ParticipantId),
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
            && item.DeclinedAt == null
            && item.ExpiresAt > DateTimeOffset.UtcNow)
        .Join(database.Challenges, invitation => invitation.ChallengeId, challenge => challenge.Id,
            (invitation, challenge) => new { invitation, challenge })
        .Where(pair => pair.challenge.Status == ChallengeStatus.PendingPlayers)
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
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    var user = await users.GetUserAsync(principal);
    if (user?.Email is null) return Results.Unauthorized();
    var invitation = await database.ChallengeInvitations
        .SingleOrDefaultAsync(item => item.Id == invitationId, cancellationToken);
    if (invitation is null || invitation.InviteeEmail?.Equals(user.Email, StringComparison.OrdinalIgnoreCase) != true)
        return Results.NotFound();

    var joinResult = await JoinChallengeAsync(invitation, user, database, recalculator, logger, cancellationToken);
    if (joinResult.Error is not null) return joinResult.Error;
    await hub.Clients.Group($"challenge:{invitation.ChallengeId}")
        .SendAsync("challenge.updated", new { challengeId = invitation.ChallengeId }, cancellationToken);
    await hub.Clients.Group($"user:{invitation.InvitedByUserId}")
        .SendAsync("challenge.invitationReceived", new { challengeId = invitation.ChallengeId, accepted = true }, cancellationToken);
    return Results.Ok(new { challengeId = invitation.ChallengeId });
});

me.MapPost("/invitations/{invitationId:guid}/decline", async (
    Guid invitationId, ClaimsPrincipal principal, UserManager<ApplicationUser> users,
    ChallengeDbContext database, IHubContext<ChallengeHub> hub, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    var user = await users.GetUserAsync(principal);
    if (user?.Email is null) return Results.Unauthorized();
    await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
    var invitation = await database.ChallengeInvitations.SingleOrDefaultAsync(item => item.Id == invitationId, cancellationToken);
    if (invitation is null || invitation.InviteeEmail?.Equals(user.Email, StringComparison.OrdinalIgnoreCase) != true)
        return Results.NotFound();
    if (invitation.RevokedAt.HasValue || invitation.RedeemedAt.HasValue || invitation.DeclinedAt.HasValue
        || invitation.ExpiresAt <= DateTimeOffset.UtcNow)
        return Results.Conflict(new { title = "This invitation is no longer pending." });
    var challenge = await database.Challenges.SingleAsync(item => item.Id == invitation.ChallengeId, cancellationToken);
    invitation.DeclinedAt = DateTimeOffset.UtcNow;
    database.AuditLogs.Add(new AuditLog
    {
        ActorId = user.Id, ChallengeId = challenge.Id, EntityType = "ChallengeInvitation",
        EntityId = invitation.Id.ToString(), Action = "Declined",
    });
    database.UserNotifications.Add(new UserNotification
    {
        UserId = invitation.InvitedByUserId, ChallengeId = challenge.Id, Type = "challenge.invitation.declined",
        Title = "Your invitation was declined", Message = $"{user.DisplayName} declined the invitation to {challenge.Name}.",
        RelatedEntityId = invitation.Id.ToString(),
    });
    try
    {
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    catch (Exception exception) when (IsSerializationFailure(exception))
    {
        logger.LogWarning(exception, "Concurrent invitation decline for challenge {ChallengeId}", challenge.Id);
        return Results.Conflict(new { title = "The invitation changed. Refresh and try again." });
    }
    await hub.Clients.Group($"user:{invitation.InvitedByUserId}")
        .SendAsync("challenge.invitationReceived", new { challengeId = challenge.Id }, cancellationToken);
    await hub.Clients.Group($"user:{user.Id}")
        .SendAsync("challenge.invitationReceived", new { challengeId = challenge.Id }, cancellationToken);
    await hub.Clients.Group($"challenge:{challenge.Id}")
        .SendAsync("challenge.updated", new { challengeId = challenge.Id }, cancellationToken);
    return Results.NoContent();
});

app.MapGet("/api/achievement-catalog", async (ChallengeDbContext database, CancellationToken cancellationToken) =>
    Results.Ok(await database.AchievementDefinitions.Where(item => item.ChallengeId == null)
        .OrderBy(item => item.Code)
        .Select(item => new { item.Code, item.Name, item.Description, item.Category, Frequency = item.Frequency.ToString(), item.DefaultPoints })
        .ToListAsync(cancellationToken))).RequireAuthorization();

app.MapGet("/api/geography", async (ChallengeDbContext database, CancellationToken cancellationToken) =>
{
    var continents = await database.Continents.AsNoTracking().OrderBy(item => item.Name)
        .Select(item => new { item.Code, item.Name }).ToListAsync(cancellationToken);
    var countries = await database.Countries.AsNoTracking().OrderBy(item => item.Name)
        .Select(item => new { item.Code, item.Name, item.ContinentCode }).ToListAsync(cancellationToken);
    return Results.Ok(new { continents, countries });
}).RequireAuthorization();

var clubs = app.MapGroup("/api/clubs").RequireAuthorization();
clubs.MapGet("/", async (string? search, string? countryCode, ChallengeDbContext database, CancellationToken cancellationToken) =>
{
   if (search?.Trim().Length > 120 || countryCode?.Trim().Length > 8)
       return Results.ValidationProblem(new Dictionary<string, string[]> { ["search"] = ["The search or country code is too long."] });
   var query = database.Clubs.AsNoTracking().AsQueryable();
   if (!string.IsNullOrWhiteSpace(search))
   {
       var term = search.Trim().ToUpperInvariant();
       query = query.Where(item => item.NormalizedName.Contains(term) || item.NormalizedIdentityLabel.Contains(term));
   }
   if (!string.IsNullOrWhiteSpace(countryCode))
   {
       var code = countryCode.Trim().ToUpperInvariant();
       query = query.Where(item => item.CountryCode == code);
   }
   var items = await (from club in query
       join country in database.Countries.AsNoTracking() on club.CountryCode equals country.Code
       orderby club.Name, club.CountryCode, club.IdentityLabel, club.Id
       select new { club.Id, club.Name, club.IdentityLabel, club.CountryCode, CountryName = country.Name }).Take(100).ToArrayAsync(cancellationToken);
   return Results.Ok(items);
});
clubs.MapPost("/", async (ClubCatalogRequest request, ChallengeDbContext database, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
   var name = request.Name?.Trim();
   var identityLabel = request.IdentityLabel?.Trim() ?? string.Empty;
   var code = request.CountryCode?.Trim().ToUpperInvariant();
   if (string.IsNullOrWhiteSpace(name) || name.Length > 120 || name.ToUpperInvariant().Length > 120
       || identityLabel.Length > 80 || identityLabel.ToUpperInvariant().Length > 80
       || string.IsNullOrWhiteSpace(code) || code.Length > 8)
       return Results.ValidationProblem(new Dictionary<string, string[]> { ["club"] = ["Enter a valid club name and country code."] });
   var country = await database.Countries.AsNoTracking().SingleOrDefaultAsync(item => item.Code == code, cancellationToken);
   if (country is null)
       return Results.ValidationProblem(new Dictionary<string, string[]> { ["countryCode"] = ["Choose a country from the country catalog."] });
   var normalizedName = name.ToUpperInvariant();
   var normalizedIdentityLabel = identityLabel.ToUpperInvariant();
   if (string.IsNullOrEmpty(identityLabel) && await database.Clubs.AnyAsync(
       item => item.CountryCode == code && item.NormalizedName == normalizedName, cancellationToken))
       return Results.Conflict(new { title = "A club with this name and country already exists. Add an identity label to create a distinct club." });
   var club = new Club
   {
       Name = name,
       NormalizedName = normalizedName,
       IdentityLabel = identityLabel,
       NormalizedIdentityLabel = normalizedIdentityLabel,
       CountryCode = code,
   };
   database.Clubs.Add(club);
   try { await database.SaveChangesAsync(cancellationToken); }
   catch (Exception exception) when (IsUniqueConstraintViolation(exception, "IX_Clubs_CountryCode_NormalizedName_NormalizedIdentityLabel"))
   {
       logger.LogInformation("Duplicate club catalog entry for country {CountryCode}", code);
       return Results.Conflict(new { title = "A club with this name, country, and identity label already exists in the catalog." });
   }
   return Results.Created($"/api/clubs/{club.Id}", new { club.Id, club.Name, club.IdentityLabel, club.CountryCode, CountryName = country.Name });
});
clubs.MapGet("/{clubId:guid}", async (Guid clubId, ChallengeDbContext database, CancellationToken cancellationToken) =>
{
   var club = await (from entry in database.Clubs.AsNoTracking()
       join country in database.Countries.AsNoTracking() on entry.CountryCode equals country.Code
       where entry.Id == clubId
       select new { entry.Id, entry.Name, entry.IdentityLabel, entry.CountryCode, CountryName = country.Name }).SingleOrDefaultAsync(cancellationToken);
   return club is null ? Results.NotFound() : Results.Ok(club);
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
    if (request.Description?.Trim().Length > 500 || request.TrainerName?.Trim().Length > 80)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["challenge"] = ["The description or trainer name is too long."] });

    var user = await users.GetUserAsync(principal);
    if (user is null) return Results.Unauthorized();

    var officialDefinitions = await database.AchievementDefinitions.Where(item => item.ChallengeId == null).ToListAsync(cancellationToken);
    var selectedCodes = request.AchievementCodes?.ToHashSet(StringComparer.Ordinal);
    if (selectedCodes is not null && selectedCodes.Any(code => !officialDefinitions.Any(item => item.Code == code)))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["achievementCodes"] = ["Select only achievements from the official catalog."] });
    await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
    var challenge = new Challenge
    {
        Name = normalizedName,
        Description = request.Description?.Trim(),
        StartSeasonYear = request.StartSeasonYear,
        CreatedByUserId = user.Id,
        Status = request.SaveAsDraft ? ChallengeStatus.Draft : ChallengeStatus.PendingPlayers,
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
        RequiresStartingStatus = true,
    });
    database.ChallengeRules.AddRange(officialDefinitions.Select(definition => new ChallengeRule
    {
        ChallengeId = challenge.Id,
        AchievementCode = definition.Code,
        Points = definition.DefaultPoints,
        IsEnabled = definition.IsEnabled && (selectedCodes is null || selectedCodes.Contains(definition.Code)),
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
        NewData = JsonSerializer.Serialize(new { challenge.Name, challenge.StartSeasonYear, challenge.TargetPoints, challenge.Status }),
    });
    await database.SaveChangesAsync(cancellationToken);
    await transaction.CommitAsync(cancellationToken);
    return Results.Created($"/api/challenges/{challenge.Id}", new { id = challenge.Id });
});

challenges.MapPost("/{challengeId:guid}/publish", async (
    Guid challengeId, PublishDraftRequest request, ClaimsPrincipal principal,
    ChallengeDbContext database, IHubContext<ChallengeHub> hub, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    var userId = GetUserId(principal);
    await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
    var participant = await GetParticipantAsync(database, challengeId, userId, cancellationToken);
    if (participant is null || participant.Role != ParticipantRole.Administrator) return Results.Forbid();
    var challenge = await database.Challenges.SingleAsync(item => item.Id == challengeId, cancellationToken);
    if (challenge.Status != ChallengeStatus.Draft)
        return Results.Conflict(new { title = "Only a draft can be published." });
    if (request.Version is null || challenge.Version != request.Version)
        return Results.Conflict(new { title = "The challenge changed. Refresh before publishing." });
    if (await database.ChallengeParticipants.CountAsync(item => item.ChallengeId == challengeId, cancellationToken) != 1)
        return Results.Conflict(new { title = "A draft must have exactly one participant before publication." });
    challenge.Status = ChallengeStatus.PendingPlayers;
    challenge.Version++;
    database.ActivityEvents.Add(new ActivityEvent
    {
        ChallengeId = challengeId, ParticipantId = participant.Id, Type = "challenge.published",
        Message = "The challenge was published and is ready for an invitation.",
    });
    database.AuditLogs.Add(new AuditLog
    {
        ActorId = userId, ChallengeId = challengeId, EntityType = "Challenge", EntityId = challengeId.ToString(),
        Action = "Published", OldData = JsonSerializer.Serialize(new { Status = ChallengeStatus.Draft }),
        NewData = JsonSerializer.Serialize(new { challenge.Status, challenge.Version }),
    });
    try
    {
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    catch (Exception exception) when (exception is DbUpdateConcurrencyException || IsSerializationFailure(exception))
    {
        logger.LogWarning(exception, "Concurrent draft publication for challenge {ChallengeId}", challengeId);
        return Results.Conflict(new { title = "The challenge changed. Refresh before publishing." });
    }
    await hub.Clients.Group($"challenge:{challengeId}").SendAsync("challenge.updated", new { challengeId }, cancellationToken);
    await hub.Clients.Group($"user:{userId}").SendAsync("challenge.updated", new { challengeId }, cancellationToken);
    return Results.Ok(new { Status = challenge.Status.ToString(), challenge.Version });
});

challenges.MapPost("/{challengeId:guid}/invites", async (
    Guid challengeId,
    InviteCreateRequest? request,
    ClaimsPrincipal principal,
    UserManager<ApplicationUser> users,
    ChallengeDbContext database,
    IConfiguration configuration,
    IHubContext<ChallengeHub> hub,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    var user = await users.GetUserAsync(principal);
    if (user is null) return Results.Unauthorized();
    await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
    var participant = await GetParticipantAsync(database, challengeId, user.Id, cancellationToken);
    if (participant is null || participant.Role != ParticipantRole.Administrator) return Results.Forbid();
    var challenge = await database.Challenges.SingleAsync(item => item.Id == challengeId, cancellationToken);
    var acceptedCount = await database.ChallengeParticipants.CountAsync(item => item.ChallengeId == challengeId, cancellationToken);
    if (acceptedCount >= 2 || challenge.Status != ChallengeStatus.PendingPlayers)
        return Results.Conflict(new { title = "This challenge cannot accept another player." });
    if (request?.ExpiresInDays is < 1 or > 30)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["expiresInDays"] = ["Choose an invitation lifetime between 1 and 30 days."] });
    if (!string.IsNullOrWhiteSpace(request?.Email)
        && (request.Email.Trim().Length > 256 || !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(request.Email.Trim())))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["email"] = ["Enter a valid email address."] });

    var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    var invite = new ChallengeInvitation
    {
        ChallengeId = challengeId,
        InvitedByUserId = user.Id,
        TokenHash = HashToken(token),
        InviteeEmail = string.IsNullOrWhiteSpace(request?.Email) ? null : request.Email.Trim().ToUpperInvariant(),
        ExpiresAt = DateTimeOffset.UtcNow.AddDays(request?.ExpiresInDays ?? 7),
    };
    database.ChallengeInvitations.Add(invite);
    database.AuditLogs.Add(new AuditLog
    {
        ActorId = user.Id, ChallengeId = challengeId, EntityType = "ChallengeInvitation", EntityId = invite.Id.ToString(),
        Action = "Created", NewData = JsonSerializer.Serialize(new { invite.InviteeEmail, invite.ExpiresAt }),
    });
    Guid? notifiedUserId = null;
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
            notifiedUserId = invitedUser.Id;
        }
    }
    try
    {
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    catch (Exception exception) when (IsSerializationFailure(exception))
    {
        logger.LogWarning(exception, "Concurrent invitation creation for challenge {ChallengeId}", challengeId);
        return Results.Conflict(new { title = "The challenge changed. Refresh and try again." });
    }
    if (notifiedUserId is { } recipientId)
        await hub.Clients.Group($"user:{recipientId}")
            .SendAsync("challenge.invitationReceived", new { challengeId, invitationId = invite.Id }, cancellationToken);

    var baseUrl = configuration["Frontend:BaseUrl"] ?? "http://localhost:5173";
    var url = $"{baseUrl.TrimEnd('/')}/dashboard?invite={Uri.EscapeDataString(token)}";
    return Results.Ok(new { id = invite.Id, url, expiresAt = invite.ExpiresAt });
});

challenges.MapGet("/{challengeId:guid}/invites", async (
    Guid challengeId, ClaimsPrincipal principal, ChallengeDbContext database, CancellationToken cancellationToken) =>
{
    var participant = await GetParticipantAsync(database, challengeId, GetUserId(principal), cancellationToken);
    if (participant is null || participant.Role != ParticipantRole.Administrator) return Results.Forbid();
    var invitations = await database.ChallengeInvitations.Where(item => item.ChallengeId == challengeId)
        .OrderByDescending(item => item.CreatedAt).ToListAsync(cancellationToken);
    var now = DateTimeOffset.UtcNow;
    return Results.Ok(invitations.Select(item => new
    {
        item.Id, item.InviteeEmail, item.CreatedAt, item.ExpiresAt,
        status = item.RedeemedAt.HasValue ? "Accepted" : item.DeclinedAt.HasValue ? "Declined"
            : item.RevokedAt.HasValue ? "Revoked" : item.ExpiresAt <= now ? "Expired" : "Pending",
    }));
});

challenges.MapPost("/{challengeId:guid}/invites/{invitationId:guid}/revoke", async (
    Guid challengeId, Guid invitationId, ClaimsPrincipal principal, ChallengeDbContext database,
    IHubContext<ChallengeHub> hub, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    var userId = GetUserId(principal);
    await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
    var participant = await GetParticipantAsync(database, challengeId, userId, cancellationToken);
    if (participant is null || participant.Role != ParticipantRole.Administrator) return Results.Forbid();
    var invitation = await database.ChallengeInvitations.SingleOrDefaultAsync(
        item => item.Id == invitationId && item.ChallengeId == challengeId, cancellationToken);
    if (invitation is null) return Results.NotFound();
    if (invitation.RevokedAt.HasValue || invitation.RedeemedAt.HasValue || invitation.DeclinedAt.HasValue
        || invitation.ExpiresAt <= DateTimeOffset.UtcNow)
        return Results.Conflict(new { title = "This invitation is no longer pending." });
    invitation.RevokedAt = DateTimeOffset.UtcNow;
    database.AuditLogs.Add(new AuditLog
    {
        ActorId = userId, ChallengeId = challengeId, EntityType = "ChallengeInvitation",
        EntityId = invitation.Id.ToString(), Action = "Revoked",
    });
    var recipientId = invitation.InviteeEmail is null ? null : await database.Users
        .Where(item => item.NormalizedEmail == invitation.InviteeEmail).Select(item => (Guid?)item.Id)
        .SingleOrDefaultAsync(cancellationToken);
    if (recipientId.HasValue)
        database.UserNotifications.Add(new UserNotification
        {
            UserId = recipientId.Value, ChallengeId = challengeId, Type = "challenge.invitation.revoked",
            Title = "An invitation was revoked", Message = "An invitation to a challenge is no longer available.",
            RelatedEntityId = invitation.Id.ToString(),
        });
    try
    {
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    catch (Exception exception) when (IsSerializationFailure(exception))
    {
        logger.LogWarning(exception, "Concurrent invitation revocation for challenge {ChallengeId}", challengeId);
        return Results.Conflict(new { title = "The invitation changed. Refresh and try again." });
    }
    if (recipientId.HasValue)
        await hub.Clients.Group($"user:{recipientId.Value}")
            .SendAsync("challenge.invitationReceived", new { challengeId }, cancellationToken);
    await hub.Clients.Group($"challenge:{challengeId}")
        .SendAsync("challenge.updated", new { challengeId }, cancellationToken);
    return Results.NoContent();
});

challenges.MapPost("/join", async (
    JoinChallengeRequest request,
    ClaimsPrincipal principal,
    UserManager<ApplicationUser> users,
    ChallengeDbContext database,
    ScoreRecalculator recalculator,
    IHubContext<ChallengeHub> hub,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Token)) return Results.BadRequest(new { title = "An invitation token is required." });
    var user = await users.GetUserAsync(principal);
    if (user is null) return Results.Unauthorized();
    var hash = HashToken(request.Token.Trim());
    var invitation = await database.ChallengeInvitations.SingleOrDefaultAsync(
        item => item.TokenHash == hash, cancellationToken);
    if (invitation is null) return Results.NotFound(new { title = "This invitation is invalid." });
    var result = await JoinChallengeAsync(invitation, user, database, recalculator, logger, cancellationToken);
    if (result.Error is not null) return result.Error;
    await hub.Clients.Group($"challenge:{invitation.ChallengeId}")
        .SendAsync("challenge.updated", new { challengeId = invitation.ChallengeId }, cancellationToken);
    await hub.Clients.Group($"user:{invitation.InvitedByUserId}")
        .SendAsync("challenge.invitationReceived", new { challengeId = invitation.ChallengeId, accepted = true }, cancellationToken);
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
            challenge.IsCorrectionMode,
            challenge.Version,
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
    ScoreRecalculator recalculator,
    CancellationToken cancellationToken) =>
{
    await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
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
        .Select(item => new { item.ParticipantId, item.SeasonIndex, item.Points, item.Status, item.SourceType, item.SourceKey, item.RuleVersion })
        .ToListAsync(cancellationToken);
    var claims = await database.AchievementClaims.Where(item => participantIds.Contains(item.ParticipantId))
        .Select(item => new { item.Id, item.AchievementCode, item.ManagedCompetitionMatches, item.ManagedDecidingMatch,
            item.FirstSeasonAfterPromotion, item.CompetitionLeaguePosition, item.WonFirstDivisionTitle,
            item.WonLeagueTitleUnbeaten, item.CupWonInLowerDivision, item.PromotionDestinationLevel, item.PromotionStreakStartSeason,
            item.ReachedContinentalMainPhase, item.WonContinentalTitlesWithBothClubs, item.StartingCareerClub,
            item.FirstDivisionExperience, item.HigherDivisionClubMove, item.FirstMajorClubTrophy, item.WonTenMajorTrophies,
            item.StayedWithClubFromThirdToFirst, item.OfficialManagerOfYear, item.CompetitionResultKind })
        .ToDictionaryAsync(item => item.Id, cancellationToken);
    var definitions = await database.AchievementDefinitions.Where(item => item.ChallengeId == null || item.ChallengeId == challengeId)
        .Select(item => new { item.Code, item.Name }).ToDictionaryAsync(item => item.Code, item => item.Name, cancellationToken);
    var ruleNames = (await database.ChallengeRules.Where(item => item.ChallengeId == challengeId && item.NameOverride != null)
        .Select(item => new { item.VersionNumber, item.AchievementCode, item.NameOverride })
        .ToListAsync(cancellationToken)).ToDictionary(item => (item.VersionNumber, item.AchievementCode), item => item.NameOverride);
    var cupMinimums = (await database.ChallengeRules.Where(item => item.ChallengeId == challengeId
            && item.MinimumCupMatchesOverride != null)
        .Select(item => new { item.VersionNumber, item.AchievementCode, item.MinimumCupMatchesOverride })
        .ToListAsync(cancellationToken)).ToDictionary(item => (item.VersionNumber, item.AchievementCode), item => item.MinimumCupMatchesOverride!.Value);
    var seasonRecords = await (
        from season in database.CareerSeasons
        join career in database.Careers on season.CareerId equals career.Id
        where participantIds.Contains(career.ParticipantId)
        select new { career.ParticipantId, season.SeasonIndex, season.Status }
    ).ToListAsync(cancellationToken);
    var recordedSeasonIndices = seasonRecords.Select(item => item.SeasonIndex).Distinct().ToArray();
    int? commonSeasonIndex = participantIds.Length == 2
        ? seasonRecords.Where(item => item.Status == SeasonStatus.Completed)
            .GroupBy(item => item.SeasonIndex)
            .Where(group => group.Select(item => item.ParticipantId).Distinct().Count() == 2)
            .Select(group => (int?)group.Key).Max()
        : null;
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

    var currencyMismatchSeasons = events
        .Where(item => item.SourceType == "Duel" && item.Status == "Pending"
            && item.SourceKey.StartsWith("largest-outgoing-transfer:", StringComparison.Ordinal))
        .Select(item => item.SeasonIndex).Distinct().Order().ToArray();
    var totals = participants.Select(participant =>
    {
        var own = events.Where(item => item.ParticipantId == participant.Id).ToArray();
        return new
        {
            participantId = participant.Id,
            confirmedPoints = own.Where(item => item.Status == "Confirmed").Sum(item => item.Points),
            pendingPoints = own.Where(item => item.Status == "Pending").Sum(item => item.Points),
            basePoints = own.Where(item => item.Status == "Confirmed" && item.SourceType == "Achievement").Sum(item => item.Points),
            bonusPoints = own.Where(item => item.Status == "Confirmed" && item.SourceType == "FirstBonus").Sum(item => item.Points),
            duelPoints = own.Where(item => item.Status == "Confirmed" && item.SourceType == "Duel").Sum(item => item.Points),
            checkpointPoints = own.Where(item => item.Status == "Confirmed" && item.SourceType == "Checkpoint").Sum(item => item.Points),
            commonSeasonPoints = commonSeasonIndex.HasValue
                ? (int?)own.Where(item => item.Status == "Confirmed" && item.SeasonIndex <= commonSeasonIndex.Value).Sum(item => item.Points)
                : null,
        };
    }).ToArray();
    var challenge = await database.Challenges.AsNoTracking().SingleAsync(item => item.Id == challengeId, cancellationToken);
    var liveResult = challenge.Status is ChallengeStatus.Completed or ChallengeStatus.Archived
        ? null
        : RaceScoringEngine.Calculate(await recalculator.GetInputAsync(challengeId, cancellationToken));
    var breakdown = events.OrderByDescending(item => item.SeasonIndex).ThenBy(item => item.ParticipantId)
        .ThenBy(item => item.SourceType).ThenBy(item => item.SourceKey).Select(item =>
        {
            string? code = null;
            bool legacyParticipation = false;
            if (item.SourceType == "Achievement")
            {
                if (item.SourceKey.StartsWith("automatic:", StringComparison.Ordinal))
                    code = item.SourceKey["automatic:".Length..];
                else if (Guid.TryParseExact(item.SourceKey, "N", out var claimId))
                {
                    var claim = claims.GetValueOrDefault(claimId);
                    code = claim?.AchievementCode;
                    legacyParticipation = code switch
                    {
                        "G001" or "G002" or "G003" or "G004" => claim?.ManagedCompetitionMatches is null,
                        "G008" or "G009" or "G010" or "G023" => claim?.ManagedDecidingMatch is null,
                        "G011" or "G016" or "G019" => claim?.FirstSeasonAfterPromotion is null,
                        "G012" => claim?.CompetitionLeaguePosition is null,
                        "G013" => claim?.WonFirstDivisionTitle is null,
                        "G014" => claim?.WonLeagueTitleUnbeaten is null,
                        "G015" => claim?.CupWonInLowerDivision is null,
                        "G005" or "G006" or "G007" => claim?.PromotionDestinationLevel is null,
                        "G017" or "G018" => claim?.PromotionStreakStartSeason is null,
                        "G025" or "G026" or "G027" => claim?.ReachedContinentalMainPhase is null,
                        "G032" => claim?.WonContinentalTitlesWithBothClubs is null,
                        "G037" or "G041" => claim?.StartingCareerClub is null,
                        "G038" => claim?.FirstDivisionExperience is null,
                        "G039" => claim?.HigherDivisionClubMove is null,
                        "G040" => claim?.FirstMajorClubTrophy is null,
                        "G048" => claim?.WonTenMajorTrophies is null,
                        "G050" => claim?.StayedWithClubFromThirdToFirst is null,
                        "G051" => claim?.OfficialManagerOfYear is null,
                        "G020" or "G021" or "G022" or "G024" or "G028" or "G029" or "G030" or "G031"
                            or "G034" or "G035" or "G036" => claim?.CompetitionResultKind is null,
                        _ => false,
                    };
                }
            }
            var label = item.SourceType switch
            {
                "Achievement" when code is not null && definitions.TryGetValue(code, out var name)
                    => $"{code} · {ruleNames.GetValueOrDefault((item.RuleVersion, code)) ?? name}",
                "FirstBonus" => $"First bonus · {(item.SourceKey.StartsWith("first-", StringComparison.Ordinal) ? item.SourceKey[6..] : item.SourceKey).Replace('-', ' ')}",
                "Duel" => $"Season duel · {item.SourceKey.Split(':')[0].Replace('-', ' ')}",
                "Checkpoint" => $"Checkpoint · {item.SourceKey.Split(':').Last()} points",
                _ => $"{item.SourceType} · {item.SourceKey}",
            };
            var explanation = item.Status == "Pending" ? item.SourceType switch
            {
                "Duel" => "Transfer currencies differ within a season or between managers; the duel cannot be compared yet.",
                "Checkpoint" when events.Any(other => other.Status == "Pending"
                    && other.SourceType != "Checkpoint" && other.SeasonIndex <= item.SeasonIndex)
                    => "Earlier or same-season scoring is still pending, so the checkpoint result may change.",
                "FirstBonus" or "Checkpoint" => "Both managers must complete this season before the result can be confirmed.",
                _ => "This award is awaiting confirmation.",
            } : item.SourceType == "Achievement" && item.Points == 0
                ? code is "G008" or "G009" or "G015"
                    && Guid.TryParseExact(item.SourceKey, "N", out var cupClaimId)
                    && claims.GetValueOrDefault(cupClaimId)?.ManagedCompetitionMatches is int matches
                    && matches < cupMinimums.GetValueOrDefault((item.RuleVersion, code), 2)
                    ? $"Not awarded because fewer than {cupMinimums.GetValueOrDefault((item.RuleVersion, code), 2)} cup matches were managed."
                    : code == "G040"
                    ? "Not awarded because an earlier major trophy is recorded for this club."
                    : code == "G013"
                    ? "Not awarded because same-season title or cup records conflict with this club or country."
                    : code == "G038"
                    ? "Not awarded because a first-division contract or match is recorded in an earlier season."
                    : code == "G039"
                    ? "Not awarded because an earlier contract or match is in a higher division than the stated previous best."
                    : code == "G050"
                    ? "Not awarded because matches at another club are recorded during the claimed progression."
                    : code is "G037" or "G041"
                    ? "Not awarded because the recorded career move no longer meets its career requirements."
                    : "Not awarded because an incompatible achievement was recorded in the same season." : null;
            if (legacyParticipation)
                explanation = string.Join(" ", new[] { explanation, "This older record has no achievement condition attestation." }
                    .Where(text => !string.IsNullOrEmpty(text)));
            return new
            {
                item.ParticipantId, item.SeasonIndex, item.Points, item.Status, item.SourceType,
                item.SourceKey, item.RuleVersion, label, explanation,
            };
        }).ToArray();
    await transaction.CommitAsync(cancellationToken);
    return Results.Ok(new
    {
        participants, series, currencyMismatchSeasons, commonSeasonIndex, totals, breakdown,
        victoryPending = liveResult?.VictoryPending ?? false,
        pendingVictorySeasonIndex = liveResult?.VictoryPending == true ? liveResult.WinningSeasonIndex : null,
    });
});

challenges.MapPost("/{challengeId:guid}/reopen", async (
    Guid challengeId, ChallengeCorrectionRequest request, ClaimsPrincipal principal,
    ChallengeDbContext database, ScoreRecalculator recalculator, IHubContext<ChallengeHub> hub,
    ILogger<Program> logger, CancellationToken cancellationToken) =>
    await ChangeCorrectionModeAsync(challengeId, request, true, principal, database, recalculator, hub, logger, cancellationToken));

challenges.MapPost("/{challengeId:guid}/finish-corrections", async (
    Guid challengeId, ChallengeCorrectionRequest request, ClaimsPrincipal principal,
    ChallengeDbContext database, ScoreRecalculator recalculator, IHubContext<ChallengeHub> hub,
    ILogger<Program> logger, CancellationToken cancellationToken) =>
    await ChangeCorrectionModeAsync(challengeId, request, false, principal, database, recalculator, hub, logger, cancellationToken));

challenges.MapPost("/{challengeId:guid}/archive", async (
    Guid challengeId, ChallengeCorrectionRequest request, ClaimsPrincipal principal,
    ChallengeDbContext database, IHubContext<ChallengeHub> hub, ILogger<Program> logger, CancellationToken cancellationToken) =>
    await ChangeArchiveAsync(challengeId, request, true, principal, database, hub, logger, cancellationToken));
challenges.MapPost("/{challengeId:guid}/restore", async (
    Guid challengeId, ChallengeCorrectionRequest request, ClaimsPrincipal principal,
    ChallengeDbContext database, IHubContext<ChallengeHub> hub, ILogger<Program> logger, CancellationToken cancellationToken) =>
    await ChangeArchiveAsync(challengeId, request, false, principal, database, hub, logger, cancellationToken));

challenges.MapGet("/{challengeId:guid}/rules", async (
    Guid challengeId, ClaimsPrincipal principal, ChallengeDbContext database, CancellationToken cancellationToken) =>
{
    if (await GetParticipantAsync(database, challengeId, GetUserId(principal), cancellationToken) is null)
        return Results.Forbid();
    var challengeVersion = await database.Challenges.Where(item => item.Id == challengeId)
        .Select(item => item.Version).SingleAsync(cancellationToken);
    var versions = await database.ChallengeRules.Where(item => item.ChallengeId == challengeId)
        .OrderByDescending(item => item.VersionNumber).ToListAsync(cancellationToken);
    if (versions.Count == 0) return Results.Problem("No rules are configured for this challenge.", statusCode: 500);
    var latest = versions.Max(item => item.VersionNumber);
    var history = await database.AuditLogs.Where(item => item.ChallengeId == challengeId && item.EntityType == "ChallengeRules")
        .OrderByDescending(item => item.CreatedAt)
        .Select(item => new { item.ActorId, item.CreatedAt, item.NewData })
        .ToListAsync(cancellationToken);
    var historyDetails = history.Select(item =>
    {
        using var document = JsonDocument.Parse(item.NewData ?? "{}");
        var root = document.RootElement;
        return new
        {
            item.ActorId,
            item.CreatedAt,
            policy = root.TryGetProperty("Policy", out var policy) ? policy.GetString() : null,
            reason = root.TryGetProperty("Reason", out var reason) ? reason.GetString() : null,
            code = root.TryGetProperty("Code", out var code) ? code.GetString() : null,
        };
    }).ToArray();
    return Results.Ok(new
    {
        version = latest,
        challengeVersion,
        rules = RaceScoringEngine.WithSupplementalDefaults(versions.Where(item => item.VersionNumber == latest)
                .ToDictionary(item => item.AchievementCode, item => new RuleSetting(item.Points, item.IsEnabled,
                    item.NameOverride, item.DescriptionOverride, item.CategoryOverride, item.FrequencyOverride,
                    item.MinimumCupMatchesOverride)))
            .Select(item => new { AchievementCode = item.Key, item.Value.Points, item.Value.IsEnabled,
                item.Value.NameOverride, item.Value.DescriptionOverride, item.Value.CategoryOverride,
                FrequencyOverride = item.Value.FrequencyOverride?.ToString(), item.Value.MinimumCupMatchesOverride }),
        versions = versions.GroupBy(item => new { item.VersionNumber, item.EffectiveSeasonIndex })
            .Select(group => group.Key).OrderByDescending(item => item.VersionNumber),
        history = historyDetails,
    });
});
challenges.MapPost("/{challengeId:guid}/rules/preview", async (
    Guid challengeId, RuleChangeRequest request, ClaimsPrincipal principal, ChallengeDbContext database,
    ScoreRecalculator recalculator, CancellationToken cancellationToken) =>
    await ChangeRulesAsync(challengeId, request, true, principal, database, recalculator, null, null, cancellationToken));
challenges.MapPost("/{challengeId:guid}/rules", async (
    Guid challengeId, RuleChangeRequest request, ClaimsPrincipal principal, ChallengeDbContext database,
    ScoreRecalculator recalculator, IHubContext<ChallengeHub> hub, ILogger<Program> logger, CancellationToken cancellationToken) =>
    await ChangeRulesAsync(challengeId, request, false, principal, database, recalculator, hub, logger, cancellationToken));

challenges.MapGet("/{challengeId:guid}/competitions", async (
    Guid challengeId, ClaimsPrincipal principal, ChallengeDbContext database, CancellationToken cancellationToken) =>
{
    if (await GetParticipantAsync(database, challengeId, GetUserId(principal), cancellationToken) is null) return Results.Forbid();
    var competitions = await database.ChallengeCompetitions.Where(item => item.ChallengeId == challengeId)
        .OrderBy(item => item.Name).Select(item => new { item.Id, item.Name, item.Tier, item.Version }).ToListAsync(cancellationToken);
    return Results.Ok(competitions);
});
challenges.MapPost("/{challengeId:guid}/competitions", async (
    Guid challengeId, CompetitionRequest request, ClaimsPrincipal principal, ChallengeDbContext database,
    IHubContext<ChallengeHub> hub, ILogger<Program> logger, CancellationToken cancellationToken) =>
    await ChangeCompetitionAsync(challengeId, null, request, principal, database, hub, logger, cancellationToken));
challenges.MapPut("/{challengeId:guid}/competitions/{competitionId:guid}", async (
    Guid challengeId, Guid competitionId, CompetitionRequest request, ClaimsPrincipal principal, ChallengeDbContext database,
    IHubContext<ChallengeHub> hub, ILogger<Program> logger, CancellationToken cancellationToken) =>
    await ChangeCompetitionAsync(challengeId, competitionId, request, principal, database, hub, logger, cancellationToken));
challenges.MapDelete("/{challengeId:guid}/competitions/{competitionId:guid}", async (
    Guid challengeId, Guid competitionId, long version, ClaimsPrincipal principal, ChallengeDbContext database,
    IHubContext<ChallengeHub> hub, ILogger<Program> logger, CancellationToken cancellationToken) =>
    await ChangeCompetitionAsync(challengeId, competitionId, new CompetitionRequest(null, null, version),
        principal, database, hub, logger, cancellationToken, delete: true));

challenges.MapPost("/{challengeId:guid}/custom-achievements", async (
    Guid challengeId, CustomAchievementRequest request, ClaimsPrincipal principal, ChallengeDbContext database,
    ScoreRecalculator recalculator, IHubContext<ChallengeHub> hub, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
    var userId = GetUserId(principal);
    var participant = await GetParticipantAsync(database, challengeId, userId, cancellationToken);
    if (participant is null || participant.Role != ParticipantRole.Administrator) return Results.Forbid();
    var challenge = await database.Challenges.SingleAsync(item => item.Id == challengeId, cancellationToken);
    if (challenge.Status is ChallengeStatus.Completed or ChallengeStatus.Archived)
        return Results.Conflict(new { title = "This challenge is read-only." });
    if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 120
        || string.IsNullOrWhiteSpace(request.Description) || request.Description.Trim().Length > 500
        || request.Points is < 0 or > 10000 || request.EffectiveSeasonIndex is < 0 or > 100
        || !Enum.TryParse<AchievementFrequency>(request.Frequency, false, out var frequency)
        || !Enum.IsDefined(frequency))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["achievement"] = ["Enter a name, description, valid frequency, points and effective season."] });
    if (challenge.Version != request.ExpectedChallengeVersion)
        return Results.Conflict(new { title = "The challenge changed. Refresh the rules before adding an achievement." });
    var currentVersion = await database.ChallengeRules.Where(item => item.ChallengeId == challengeId)
        .MaxAsync(item => (int?)item.VersionNumber, cancellationToken);
    if (currentVersion is null) return Results.Problem("No rules are configured for this challenge.", statusCode: 500);
    if (currentVersion != request.ExpectedVersion)
        return Results.Conflict(new { title = "The rules changed. Refresh them before adding an achievement." });
    var versions = await database.ChallengeRules.Where(item => item.ChallengeId == challengeId).ToListAsync(cancellationToken);
    var definition = new AchievementDefinition
    {
        Code = $"C{Guid.NewGuid():N}", ChallengeId = challengeId, Name = request.Name.Trim(),
        Description = request.Description.Trim(), Category = "Custom", Frequency = frequency, DefaultPoints = request.Points,
    };
    var existingVersions = versions.GroupBy(rule => (rule.VersionNumber, rule.EffectiveSeasonIndex))
        .Select(group => new RuleVersionInput(group.Key.VersionNumber, group.Key.EffectiveSeasonIndex,
            group.ToDictionary(rule => rule.AchievementCode, rule => new RuleSetting(rule.Points, rule.IsEnabled,
                rule.NameOverride, rule.DescriptionOverride, rule.CategoryOverride, rule.FrequencyOverride,
                rule.MinimumCupMatchesOverride))))
        .ToArray();
    var nextVersions = CreateRuleVersions(existingVersions, request.EffectiveSeasonIndex, definition.Code,
        new RuleSetting(definition.DefaultPoints, true));
    var nextVersion = nextVersions[^1].VersionNumber;
    database.AchievementDefinitions.Add(definition);
    foreach (var version in nextVersions)
    {
        database.ChallengeRules.AddRange(version.Rules.Select(rule => new ChallengeRule
        {
            ChallengeId = challengeId, VersionNumber = version.VersionNumber, EffectiveSeasonIndex = version.EffectiveSeasonIndex,
            AchievementCode = rule.Key, Points = rule.Value.Points, IsEnabled = rule.Value.IsEnabled,
            NameOverride = rule.Value.NameOverride, DescriptionOverride = rule.Value.DescriptionOverride,
            CategoryOverride = rule.Value.CategoryOverride, FrequencyOverride = rule.Value.FrequencyOverride,
            MinimumCupMatchesOverride = rule.Value.MinimumCupMatchesOverride,
        }));
    }
    database.AuditLogs.Add(new AuditLog
    {
        ActorId = userId, ChallengeId = challengeId, EntityType = "ChallengeRules", EntityId = nextVersion.ToString(),
        Action = "VersionCreated", NewData = JsonSerializer.Serialize(new
        {
            Code = definition.Code, Policy = "FromSeason", Reason = "Custom achievement created",
            definition.Name, definition.Description, definition.Frequency, definition.DefaultPoints, request.EffectiveSeasonIndex,
        }),
    });
    database.ActivityEvents.Add(new ActivityEvent
    {
        ChallengeId = challengeId, ParticipantId = participant.Id, Type = "challenge.rulesUpdated",
        Message = $"Added the custom achievement {definition.Name}.", SeasonIndex = request.EffectiveSeasonIndex,
    });
    await QueueOtherParticipantNotificationsAsync(database, challengeId, participant.Id, "challenge.rulesUpdated",
        "Challenge rules updated", $"A custom achievement, {definition.Name}, was added.", definition.Code, cancellationToken);
    RaceScoringResult score;
    try
    {
        await database.SaveChangesAsync(cancellationToken);
        score = await recalculator.RecalculateAsync(challengeId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    catch (Exception exception) when (exception is DbUpdateConcurrencyException || IsSerializationFailure(exception)
        || IsUniqueConstraintViolation(exception, "PK_ChallengeRules"))
    {
        logger.LogWarning(exception, "Concurrent custom achievement creation for challenge {ChallengeId}", challengeId);
        return Results.Conflict(new { title = "The rules changed. Refresh them before adding an achievement." });
    }
    await hub.Clients.Group($"challenge:{challengeId}").SendAsync("challenge.rulesUpdated", new { challengeId, version = nextVersion }, cancellationToken);
    await hub.Clients.Group($"challenge:{challengeId}").SendAsync("score.updated", new { challengeId, scores = score.Participants }, cancellationToken);
    await hub.Clients.Group($"challenge:{challengeId}").SendAsync("challenge.updated", new { challengeId }, cancellationToken);
    await PublishUserUpdatesAsync(database, hub, challengeId, cancellationToken);
    return Results.Created($"/api/challenges/{challengeId}/achievements", new { definition.Code, version = nextVersion });
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
    var latestVersion = await database.ChallengeRules.Where(item => item.ChallengeId == challengeId)
        .MaxAsync(item => (int?)item.VersionNumber, cancellationToken) ?? 1;
    var achievementRules = await database.ChallengeRules.Where(item => item.ChallengeId == challengeId)
        .ToListAsync(cancellationToken);
    var rules = achievementRules.Where(item => item.VersionNumber == latestVersion)
        .ToDictionary(item => item.AchievementCode);
    var claims = await database.AchievementClaims.Where(item => item.ParticipantId == participant.Id)
        .OrderByDescending(item => item.SeasonIndex)
        .ToListAsync(cancellationToken);
    var definitions = await database.AchievementDefinitions
        .Where(item => item.ChallengeId == null || item.ChallengeId == challengeId).ToListAsync(cancellationToken);
    var milestones = await database.ScoreEvents.Where(item => item.ChallengeId == challengeId
        && item.ParticipantId == participant.Id && item.SourceType == "Achievement"
        && item.SourceKey.StartsWith("automatic:"))
        .ToDictionaryAsync(item => item.SourceKey, cancellationToken);
    var achievementCareer = await database.Careers.SingleAsync(item => item.ParticipantId == participant.Id, cancellationToken);
    var achievementTeams = await database.NationalTeamStints.Include(item => item.SeasonMatches)
        .Where(item => item.CareerId == achievementCareer.Id).ToListAsync(cancellationToken);
    var nationalMilestoneKnown = RaceScoringEngine.NationalTeamMilestone(
        achievementCareer.NationalTeam20MatchSeasonIndex, achievementTeams).HasValue;
    return Results.Ok(definitions.Where(definition => rules.ContainsKey(definition.Code)).Select(definition => new
    {
        definition.Code,
        Name = rules[definition.Code].NameOverride ?? definition.Name,
        Description = rules[definition.Code].DescriptionOverride ?? definition.Description,
        Category = rules[definition.Code].CategoryOverride ?? definition.Category,
        Frequency = (rules[definition.Code].FrequencyOverride ?? definition.Frequency).ToString(),
        CanChangeFrequency = CanChangeAchievementFrequency(definition.Code),
        CanChangeCupMinimum = definition.Code is "G008" or "G009" or "G015",
        FrequencyVersions = achievementRules.Where(rule => rule.AchievementCode == definition.Code)
            .Select(rule => new { rule.VersionNumber, rule.EffectiveSeasonIndex,
                Frequency = (rule.FrequencyOverride ?? definition.Frequency).ToString() }),
        CupMinimumVersions = achievementRules.Where(rule => rule.AchievementCode == definition.Code)
            .Select(rule => new { rule.VersionNumber, rule.EffectiveSeasonIndex,
                Minimum = rule.MinimumCupMatchesOverride ?? 2 }),
        MinimumCupMatches = rules[definition.Code].MinimumCupMatchesOverride ?? 2,
        Points = rules.GetValueOrDefault(definition.Code)?.Points ?? definition.DefaultPoints,
        IsEnabled = rules.GetValueOrDefault(definition.Code)?.IsEnabled ?? definition.IsEnabled,
        IsAutomatic = RaceScoringEngine.AutomaticMilestones.Contains(definition.Code)
            || definition.Code == "G053" || (definition.Code == "G033" && nationalMilestoneKnown)
            || (definition.Code == "G048" && milestones.ContainsKey("automatic:G048")),
        AutomaticSeasonIndex = milestones.GetValueOrDefault($"automatic:{definition.Code}")?.SeasonIndex,
        Claims = claims.Where(item => item.AchievementCode == definition.Code)
            .Select(item => new { item.Id, item.SeasonIndex, item.ClubId, item.ClubName, item.CountryCode, item.Notes, item.Version,
                item.ManagedCompetitionMatches, item.TotalCompetitionMatches, item.InChargeAtCompetitionEnd, item.ManagedDecidingMatch,
                item.FirstSeasonAfterPromotion, item.AvoidedRelegation, item.CompetitionLeaguePosition,
                item.WonFirstDivisionTitle, item.WonMajorNationalCup, item.WonLeagueTitleUnbeaten,
                item.CupWonInLowerDivision, item.DomesticCupMatchesManaged, item.PromotionDestinationLevel,
                item.PromotionWithoutTitle, item.PromotionStreakStartSeason, item.ReachedContinentalMainPhase,
                item.PriorContinentalMainPhase, item.FirstTopTierMainPhase, item.FirstContinentalTitleClub,
                item.FirstContinentalTitleCountry, item.FirstContinentalTitleClubId, item.FirstContinentalTitleSeason, item.WonContinentalTitlesWithBothClubs,
                item.StartingCareerClub, item.StartingCareerCountry, item.StartingCareerClubId, item.CompetitionId,
                item.FirstDivisionExperience, item.HigherDivisionClubMove, item.PreviousBestDivisionLevel, item.NewDivisionLevel,
                item.FirstMajorClubTrophy, item.TrophyNotSuperCup, item.WonTenMajorTrophies, item.ThirdDivisionStartSeason,
                item.StayedWithClubFromThirdToFirst, item.OfficialManagerOfYear,
                item.CompetitionResultKind, item.CompetitionResultStage }),
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
    var linkedClubIds = stints.Where(item => item.ClubId.HasValue).Select(item => item.ClubId!.Value).Distinct().ToArray();
    var clubIdentityLabels = await database.Clubs.AsNoTracking().Where(item => linkedClubIds.Contains(item.Id))
        .ToDictionaryAsync(item => item.Id, item => item.IdentityLabel, cancellationToken);
    var clubInputs = RaceScoringEngine.ResolveCareerClubIds(await ClubInputLoader.LoadAsync(database, stints,
        new Dictionary<Guid, Guid> { [career.Id] = participant.Id }, cancellationToken));
    var eligibleClubs = RaceScoringEngine.EligibleClubs(clubInputs);
    var eligibleCountries = RaceScoringEngine.EligibleCountries(clubInputs);
    var nationalTeams = await database.NationalTeamStints.Include(item => item.SeasonMatches)
        .Where(item => item.CareerId == career.Id).ToListAsync(cancellationToken);
    var countryContinents = await GetCountryContinentsAsync(database, cancellationToken);
    var geography = RaceScoringEngine.GeographicStints(clubInputs, nationalTeams);
    return Results.Ok(new
    {
        career.Id,
        career.TrainerName,
        career.CurrentClubName,
        career.CurrentCountryCode,
        career.StartingStatus,
        career.StartingStintId,
        career.StartingStatusVersion,
        career.RequiresStartingStatus,
        career.NationalTeam20MatchSeasonIndex,
        career.NationalTeamMilestoneVersion,
        nationalTeamMilestoneValid = RaceScoringEngine.QualifiesNationalTeamMilestone(career.NationalTeam20MatchSeasonIndex, nationalTeams),
        nationalTeamCalculatedSeasonIndex = RaceScoringEngine.CalculatedNationalTeamMilestone(nationalTeams),
        nationalTeamSeasonMatchesComplete = RaceScoringEngine.NationalTeamSeasonMatchesComplete(nationalTeams),
        career.ThreeContinentsSeasonIndex,
        career.ThreeContinentsMilestoneVersion,
        threeContinentsMilestoneValid = RaceScoringEngine.QualifiesThreeContinentsMilestone(
            career.ThreeContinentsSeasonIndex, geography, countryContinents),
        threeContinentsCalculatedSeasonIndex = RaceScoringEngine.CalculatedThreeContinentsMilestone(geography, countryContinents),
        clubs = stints.Select((item, index) => new
        {
            item.Id,
            item.ClubId,
            identityLabel = item.ClubId.HasValue ? clubIdentityLabels.GetValueOrDefault(item.ClubId.Value, string.Empty) : string.Empty,
            item.ClubName,
            item.CountryCode,
            item.CountryName,
            item.StartSeasonIndex,
            item.EndSeasonIndex,
            item.InitialDivisionLevel,
            item.OfficialMatches,
            item.EligibleSeasonIndex,
            item.CountryEligibleSeasonIndex,
            clubMilestoneValid = RaceScoringEngine.QualifiesClubMilestone(clubInputs[index], clubInputs),
            countryMilestoneValid = RaceScoringEngine.QualifiesCountryMilestone(clubInputs[index], clubInputs),
            clubEligibleSeasonIndex = eligibleClubs.FirstOrDefault(club =>
                RaceScoringEngine.SameCareerClub(club, clubInputs[index]))?.EligibleSeasonIndex,
            clubOfficialMatches = clubInputs.Where(club => RaceScoringEngine.SameCareerClub(club, clubInputs[index]))
                .Sum(club => (long)club.OfficialMatches),
            countryEligibleSeason = eligibleCountries.FirstOrDefault(country => Same(country.CountryCode, item.CountryCode))?.SeasonIndex,
            countryOfficialMatches = stints.Where(stint => Same(stint.CountryCode, item.CountryCode))
                .Sum(stint => (long)stint.OfficialMatches),
            item.Version,
        }),
    });
});

challenges.MapPut("/{challengeId:guid}/career/start", async (
    Guid challengeId, CareerStartRequest request, ClaimsPrincipal principal,
    ChallengeDbContext database, IHubContext<ChallengeHub> hub, ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
    var userId = GetUserId(principal);
    var participant = await GetParticipantAsync(database, challengeId, userId, cancellationToken);
    if (participant is null) return Results.Forbid();
    var challenge = await database.Challenges.SingleAsync(item => item.Id == challengeId, cancellationToken);
    if (challenge.Status is ChallengeStatus.Draft or ChallengeStatus.Completed or ChallengeStatus.Archived)
        return Results.Conflict(new { title = "Gameplay is unavailable while this challenge is a draft or read-only." });
    var career = await database.Careers.SingleAsync(item => item.ParticipantId == participant.Id, cancellationToken);
    if (career.StartingStatusVersion != request.Version)
        return Results.Conflict(new { title = "The career start changed. Refresh it before saving." });
    if (request.Status is not ("Unemployed" or "Club"))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["status"] = ["Choose an unemployed start or a starting club."] });
    if (request.Status == "Unemployed" && request.StintId.HasValue)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["stintId"] = ["An unemployed start cannot specify a starting club."] });
    if (request.Status == "Club")
    {
        var stints = await database.CareerStints.Where(item => item.CareerId == career.Id).ToListAsync(cancellationToken);
        var startingStint = stints.SingleOrDefault(item => item.Id == request.StintId);
        if (startingStint is null || startingStint.InitialDivisionLevel <= 1
            || stints.Any(item => item.StartSeasonIndex < startingStint.StartSeasonIndex))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["stintId"] = ["Choose a recorded club below the first division in the earliest recorded season."] });
        var moveStarts = await database.AchievementClaims.Where(item => item.ParticipantId == participant.Id
                && (item.AchievementCode == "G037" || item.AchievementCode == "G041") && item.StartingCareerClub != null)
            .Select(item => new { item.StartingCareerClub, item.StartingCareerCountry }).ToListAsync(cancellationToken);
        if (moveStarts.Any(item => !Same(item.StartingCareerClub, startingStint.ClubName)
            || !Same(item.StartingCareerCountry, startingStint.CountryCode)))
            return Results.Conflict(new { title = "Remove conflicting club or country move claims before changing the starting club." });
    }

    var oldData = JsonSerializer.Serialize(new { career.StartingStatus, career.StartingStintId });
    career.StartingStatus = request.Status;
    career.StartingStintId = request.StintId;
    career.StartingStatusVersion++;
    database.AuditLogs.Add(new AuditLog
    {
        ActorId = userId, ChallengeId = challengeId, EntityType = "CareerStart", EntityId = career.Id.ToString(),
        Action = "Updated", OldData = oldData,
        NewData = JsonSerializer.Serialize(new { career.StartingStatus, career.StartingStintId }),
    });
    try
    {
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    catch (Exception exception) when (exception is DbUpdateConcurrencyException || IsSerializationFailure(exception))
    {
        logger.LogWarning(exception, "Concurrent career start update for challenge {ChallengeId}", challengeId);
        return Results.Conflict(new { title = "The career changed. Refresh it before saving." });
    }
    await hub.Clients.Group($"challenge:{challengeId}").SendAsync("career.clubChanged", new { challengeId }, cancellationToken);
    return Results.Ok(new { career.StartingStatus, career.StartingStintId, career.StartingStatusVersion });
});

challenges.MapPut("/{challengeId:guid}/career/three-continents-milestone", async (
    Guid challengeId, NationalTeamMilestoneRequest request, ClaimsPrincipal principal,
    ChallengeDbContext database, ScoreRecalculator recalculator, IHubContext<ChallengeHub> hub,
    ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
    var userId = GetUserId(principal);
    var participant = await GetParticipantAsync(database, challengeId, userId, cancellationToken);
    if (participant is null) return Results.Forbid();
    var challenge = await database.Challenges.SingleAsync(item => item.Id == challengeId, cancellationToken);
    if (challenge.Status is ChallengeStatus.Draft or ChallengeStatus.Completed or ChallengeStatus.Archived)
        return Results.Conflict(new { title = "Gameplay is unavailable while this challenge is a draft or read-only." });
    var career = await database.Careers.SingleAsync(item => item.ParticipantId == participant.Id, cancellationToken);
    if (career.ThreeContinentsMilestoneVersion != request.Version)
        return Results.Conflict(new { title = "The continent milestone changed. Refresh before saving." });
    if (request.SeasonIndex is < 0 or > 100)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["seasonIndex"] = ["Choose a valid season index."] });
    var stints = await database.CareerStints.Where(item => item.CareerId == career.Id).ToListAsync(cancellationToken);
    var teams = await database.NationalTeamStints.Include(item => item.SeasonMatches)
        .Where(item => item.CareerId == career.Id).ToListAsync(cancellationToken);
    var clubInputs = await ClubInputLoader.LoadAsync(database, stints,
        new Dictionary<Guid, Guid> { [career.Id] = participant.Id }, cancellationToken);
    var geographicStints = RaceScoringEngine.GeographicStints(clubInputs, teams);
    var continents = await GetCountryContinentsAsync(database, cancellationToken);
    if (request.SeasonIndex.HasValue && !RaceScoringEngine.QualifiesThreeContinentsMilestone(request.SeasonIndex, geographicStints, continents))
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["seasonIndex"] = ["Record club or national team matches in three catalogued continents by this season, with a matching stint active in that season."],
        });
    var oldSeason = career.ThreeContinentsSeasonIndex;
    career.ThreeContinentsSeasonIndex = request.SeasonIndex;
    career.ThreeContinentsMilestoneVersion++;
    database.AuditLogs.Add(new AuditLog
    {
        ActorId = userId, ChallengeId = challengeId, EntityType = "ThreeContinentsMilestone", EntityId = career.Id.ToString(),
        Action = "Updated", OldData = JsonSerializer.Serialize(new { SeasonIndex = oldSeason }),
        NewData = JsonSerializer.Serialize(new { request.SeasonIndex }),
    });
    database.ActivityEvents.Add(new ActivityEvent
    {
        ChallengeId = challengeId, ParticipantId = participant.Id, Type = "career.clubChanged",
        Message = request.SeasonIndex.HasValue ? "Updated the three continents milestone season." : "Cleared the three continents milestone season.",
        SeasonIndex = request.SeasonIndex,
    });
    RaceScoringResult score;
    try
    {
        await database.SaveChangesAsync(cancellationToken);
        score = await recalculator.RecalculateAsync(challengeId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    catch (Exception exception) when (exception is DbUpdateConcurrencyException || IsSerializationFailure(exception))
    {
        logger.LogWarning(exception, "Concurrent continent milestone change for challenge {ChallengeId}", challengeId);
        return Results.Conflict(new { title = "The career data changed. Refresh and try again." });
    }
    await hub.Clients.Group($"challenge:{challengeId}").SendAsync("career.clubChanged", new { challengeId }, cancellationToken);
    await hub.Clients.Group($"challenge:{challengeId}").SendAsync("score.updated", new { challengeId, scores = score.Participants }, cancellationToken);
    await hub.Clients.Group($"challenge:{challengeId}").SendAsync("challenge.updated", new { challengeId }, cancellationToken);
    await PublishUserUpdatesAsync(database, hub, challengeId, cancellationToken);
    return Results.Ok(new { career.ThreeContinentsSeasonIndex, career.ThreeContinentsMilestoneVersion });
});

challenges.MapPut("/{challengeId:guid}/career/national-team-milestone", async (
    Guid challengeId, NationalTeamMilestoneRequest request, ClaimsPrincipal principal,
    ChallengeDbContext database, ScoreRecalculator recalculator, IHubContext<ChallengeHub> hub,
    ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
    var userId = GetUserId(principal);
    var participant = await GetParticipantAsync(database, challengeId, userId, cancellationToken);
    if (participant is null) return Results.Forbid();
    var challenge = await database.Challenges.SingleAsync(item => item.Id == challengeId, cancellationToken);
    if (challenge.Status is ChallengeStatus.Draft or ChallengeStatus.Completed or ChallengeStatus.Archived)
        return Results.Conflict(new { title = "Gameplay is unavailable while this challenge is a draft or read-only." });
    var career = await database.Careers.SingleAsync(item => item.ParticipantId == participant.Id, cancellationToken);
    if (career.NationalTeamMilestoneVersion != request.Version)
        return Results.Conflict(new { title = "The national team milestone changed. Refresh before saving." });
    if (request.SeasonIndex is < 0 or > 100)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["seasonIndex"] = ["Choose a valid season index."] });
    var teams = await database.NationalTeamStints.Include(item => item.SeasonMatches)
        .Where(item => item.CareerId == career.Id).ToListAsync(cancellationToken);
    if (request.SeasonIndex.HasValue && !RaceScoringEngine.QualifiesNationalTeamMilestone(request.SeasonIndex, teams))
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["seasonIndex"] = ["Record at least 20 national team matches and select the milestone season supported by the career records."],
        });
    var oldSeason = career.NationalTeam20MatchSeasonIndex;
    career.NationalTeam20MatchSeasonIndex = request.SeasonIndex;
    career.NationalTeamMilestoneVersion++;
    database.AuditLogs.Add(new AuditLog
    {
        ActorId = userId, ChallengeId = challengeId, EntityType = "NationalTeamMilestone", EntityId = career.Id.ToString(),
        Action = "Updated", OldData = JsonSerializer.Serialize(new { SeasonIndex = oldSeason }),
        NewData = JsonSerializer.Serialize(new { request.SeasonIndex }),
    });
    database.ActivityEvents.Add(new ActivityEvent
    {
        ChallengeId = challengeId, ParticipantId = participant.Id, Type = "nationalTeam.updated",
        Message = request.SeasonIndex.HasValue ? "Updated the 20 national team matches milestone season." : "Cleared the national team milestone season.",
        SeasonIndex = request.SeasonIndex,
    });
    RaceScoringResult score;
    try
    {
        await database.SaveChangesAsync(cancellationToken);
        score = await recalculator.RecalculateAsync(challengeId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    catch (Exception exception) when (exception is DbUpdateConcurrencyException || IsSerializationFailure(exception))
    {
        logger.LogWarning(exception, "Concurrent national team milestone change for challenge {ChallengeId}", challengeId);
        return Results.Conflict(new { title = "The national team data changed. Refresh and try again." });
    }
    await hub.Clients.Group($"challenge:{challengeId}").SendAsync("career.clubChanged", new { challengeId }, cancellationToken);
    await hub.Clients.Group($"challenge:{challengeId}").SendAsync("score.updated", new { challengeId, scores = score.Participants }, cancellationToken);
    await hub.Clients.Group($"challenge:{challengeId}").SendAsync("challenge.updated", new { challengeId }, cancellationToken);
    await PublishUserUpdatesAsync(database, hub, challengeId, cancellationToken);
    return Results.Ok(new { career.NationalTeam20MatchSeasonIndex, career.NationalTeamMilestoneVersion });
});

challenges.MapPut("/{challengeId:guid}/career/stints/{stintId:guid}/country-milestone", async (
    Guid challengeId, Guid stintId, CountryMilestoneRequest request, ClaimsPrincipal principal,
    ChallengeDbContext database, ScoreRecalculator recalculator, IHubContext<ChallengeHub> hub,
    ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
    var userId = GetUserId(principal);
    var participant = await GetParticipantAsync(database, challengeId, userId, cancellationToken);
    if (participant is null) return Results.Forbid();
    var challenge = await database.Challenges.SingleAsync(item => item.Id == challengeId, cancellationToken);
    if (challenge.Status is ChallengeStatus.Draft or ChallengeStatus.Completed or ChallengeStatus.Archived)
        return Results.Conflict(new { title = "Gameplay is unavailable while this challenge is a draft or read-only." });
    var career = await database.Careers.SingleAsync(item => item.ParticipantId == participant.Id, cancellationToken);
    var stints = await database.CareerStints.Where(item => item.CareerId == career.Id).ToListAsync(cancellationToken);
    var stint = stints.SingleOrDefault(item => item.Id == stintId);
    if (stint is null) return Results.NotFound();
    if (!request.Version.HasValue)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["version"] = ["A career stint version is required."] });
    if (request.Version != stint.Version)
        return Results.Conflict(new { title = "This club stint changed. Refresh it before saving your changes." });
    if (request.SeasonIndex is < 0 or > 100)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["seasonIndex"] = ["Choose a valid season index."] });
    var clubInputs = await ClubInputLoader.LoadAsync(database, stints,
        new Dictionary<Guid, Guid> { [career.Id] = participant.Id }, cancellationToken);
    var milestoneIndex = stints.FindIndex(item => item.Id == stint.Id);
    var milestone = clubInputs[milestoneIndex] with { CountryEligibleSeasonIndex = request.SeasonIndex };
    clubInputs[milestoneIndex] = milestone;
    if (request.SeasonIndex.HasValue && !RaceScoringEngine.QualifiesCountryMilestone(milestone,
            clubInputs))
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["seasonIndex"] = ["Record at least 20 matches in this country across stints started by the milestone season, including a match in this stint."],
        });
    var oldSeason = stint.CountryEligibleSeasonIndex;
    stint.CountryEligibleSeasonIndex = request.SeasonIndex;
    stint.Version++;
    database.AuditLogs.Add(new AuditLog
    {
        ActorId = userId, ChallengeId = challengeId, EntityType = "CountryMatchMilestone", EntityId = stint.Id.ToString(),
        Action = "Updated", OldData = JsonSerializer.Serialize(new { SeasonIndex = oldSeason }),
        NewData = JsonSerializer.Serialize(new { request.SeasonIndex }),
    });
    database.ActivityEvents.Add(new ActivityEvent
    {
        ChallengeId = challengeId, ParticipantId = participant.Id, Type = "career.clubChanged",
        Message = request.SeasonIndex.HasValue ? "Updated a country match milestone season." : "Cleared a country match milestone season.",
        ClubName = stint.ClubName, SeasonIndex = request.SeasonIndex,
    });
    RaceScoringResult score;
    try
    {
        await database.SaveChangesAsync(cancellationToken);
        score = await recalculator.RecalculateAsync(challengeId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    catch (Exception exception) when (exception is DbUpdateConcurrencyException || IsSerializationFailure(exception))
    {
        logger.LogWarning(exception, "Concurrent country milestone update for challenge {ChallengeId}, stint {StintId}", challengeId, stintId);
        return Results.Conflict(new { title = "The career changed. Refresh and try again." });
    }
    await hub.Clients.Group($"challenge:{challengeId}").SendAsync("career.clubChanged", new { challengeId, stintId }, cancellationToken);
    await hub.Clients.Group($"challenge:{challengeId}").SendAsync("score.updated", new { challengeId, scores = score.Participants }, cancellationToken);
    await PublishUserUpdatesAsync(database, hub, challengeId, cancellationToken);
    return Results.Ok(new { stint.Id, stint.CountryEligibleSeasonIndex, stint.Version });
});

challenges.MapPost("/{challengeId:guid}/career/stints", async (
    Guid challengeId,
    CareerStintCreateRequest request,
    ClaimsPrincipal principal,
    ChallengeDbContext database,
    ScoreRecalculator recalculator,
    IHubContext<ChallengeHub> hub,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
    var userId = GetUserId(principal);
    var participant = await GetParticipantAsync(database, challengeId, userId, cancellationToken);
    if (participant is null) return Results.Forbid();
    var challenge = await database.Challenges.SingleAsync(item => item.Id == challengeId, cancellationToken);
    if (challenge.Status is ChallengeStatus.Draft or ChallengeStatus.Completed or ChallengeStatus.Archived)
        return Results.Conflict(new { title = "Gameplay is unavailable while this challenge is a draft or read-only." });
    if (!IsValidCareerStint(request))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["career"] = ["Enter valid club details and a milestone season within the stint, if provided."] });
    var clubError = await ValidateStintCatalogClubAsync(database, request, cancellationToken);
    if (clubError is not null)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["clubId"] = [clubError] });
    var career = await database.Careers.SingleAsync(item => item.ParticipantId == participant.Id, cancellationToken);
    var identityError = await ValidateCareerStintIdentityAsync(database, career.Id, request, null, cancellationToken);
    if (identityError is not null)
        return Results.Conflict(new { title = identityError });
    if (career.RequiresStartingStatus && career.StartingStatus is null
        && await database.CareerStints.AnyAsync(item => item.CareerId == career.Id, cancellationToken))
        return Results.Conflict(new { title = "Record your career start before adding another club." });
    if (career.StartingStintId.HasValue)
    {
        var startingSeason = await database.CareerStints.Where(item => item.Id == career.StartingStintId)
            .Select(item => item.StartSeasonIndex).SingleAsync(cancellationToken);
        if (request.StartSeasonIndex < startingSeason)
            return Results.Conflict(new { title = "This stint starts before the declared starting club. Correct your career start first." });
    }
    var stint = new CareerStint
    {
        CareerId = career.Id,
        ClubId = request.ClubId,
        ClubName = request.ClubName.Trim(),
        CountryCode = request.CountryCode.Trim().ToUpperInvariant(),
        CountryName = request.CountryName.Trim(),
        StartSeasonIndex = request.StartSeasonIndex,
        EndSeasonIndex = request.EndSeasonIndex,
        InitialDivisionLevel = request.InitialDivisionLevel,
        OfficialMatches = request.OfficialMatches,
        EligibleSeasonIndex = request.EligibleSeasonIndex,
    };
    if (!await IsValidClubMilestoneAsync(database, participant.Id, stint, cancellationToken))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["eligibleSeasonIndex"] = ["Record at least 20 matches across this club's stints started by the milestone season, including a match in the milestone stint."] });
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
        NewData = JsonSerializer.Serialize(stint),
    });
    RaceScoringResult score;
    try
    {
        await database.SaveChangesAsync(cancellationToken);
        await RefreshCurrentClubAsync(database, career, cancellationToken);
        score = await recalculator.RecalculateAsync(challengeId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    catch (Exception exception) when (exception is DbUpdateConcurrencyException || IsSerializationFailure(exception))
    {
        logger.LogWarning(exception, "Concurrent club stint creation for challenge {ChallengeId}", challengeId);
        return Results.Conflict(new { title = "The career changed. Refresh and try again." });
    }
    await hub.Clients.Group($"challenge:{challengeId}")
        .SendAsync("career.clubChanged", new { challengeId, clubName = stint.ClubName, seasonIndex = stint.StartSeasonIndex }, cancellationToken);
    await hub.Clients.Group($"challenge:{challengeId}")
        .SendAsync("score.updated", new { challengeId, scores = score.Participants }, cancellationToken);
    await hub.Clients.Group($"challenge:{challengeId}")
        .SendAsync("challenge.updated", new { challengeId }, cancellationToken);
    await PublishUserUpdatesAsync(database, hub, challengeId, cancellationToken);
    return Results.Created($"/api/challenges/{challengeId}/career/stints/{stint.Id}", new { stint.Id });
});

challenges.MapPatch("/{challengeId:guid}/career/stints/{stintId:guid}/matches", async (
    Guid challengeId,
    Guid stintId,
    CareerMatchesUpdateRequest request,
    ClaimsPrincipal principal,
    ChallengeDbContext database,
    ScoreRecalculator recalculator,
    IHubContext<ChallengeHub> hub,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    var userId = GetUserId(principal);
    await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
    var participant = await GetParticipantAsync(database, challengeId, userId, cancellationToken);
    if (participant is null) return Results.Forbid();
    var challenge = await database.Challenges.SingleAsync(item => item.Id == challengeId, cancellationToken);
    if (challenge.Status is ChallengeStatus.Draft or ChallengeStatus.Completed or ChallengeStatus.Archived)
        return Results.Conflict(new { title = "Gameplay is unavailable while this challenge is a draft or read-only." });
    var stint = await (
        from entry in database.CareerStints
        join career in database.Careers on entry.CareerId equals career.Id
        where entry.Id == stintId && career.ParticipantId == participant.Id
        select entry).SingleOrDefaultAsync(cancellationToken);
    if (stint is null) return Results.NotFound();
    if (!request.Version.HasValue)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["version"] = ["A career stint version is required."] });
    if (stint.Version != request.Version)
        return Results.Conflict(new { title = "This club stint changed. Refresh it before saving your changes." });
    if (request.OfficialMatches is < 0 or > 5000
        || request.EligibleSeasonIndex < stint.StartSeasonIndex
        || request.EligibleSeasonIndex > 100
        || (stint.EndSeasonIndex.HasValue && request.EligibleSeasonIndex > stint.EndSeasonIndex))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["career"] = ["Enter a valid match count and milestone season within this club stint."] });
    var allocatedMatches = await database.ClubSeasonStatistics.Where(item => item.CareerStintId == stint.Id)
        .SumAsync(item => (long)item.OfficialMatches, cancellationToken);
    if (allocatedMatches > request.OfficialMatches)
        return Results.Conflict(new { title = "The stint has more matches allocated to seasons than the requested total. Correct its season breakdowns first." });

    var oldData = JsonSerializer.Serialize(new { stint.OfficialMatches, stint.EligibleSeasonIndex });
    stint.OfficialMatches = request.OfficialMatches;
    stint.EligibleSeasonIndex = request.EligibleSeasonIndex;
    if (!await IsValidClubMilestoneAsync(database, participant.Id, stint, cancellationToken))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["eligibleSeasonIndex"] = ["Record at least 20 matches across this club's stints started by the milestone season, including a match in the milestone stint."] });
    stint.Version++;
    database.AuditLogs.Add(new AuditLog
    {
        ActorId = userId,
        ChallengeId = challengeId,
        EntityType = "CareerStint",
        EntityId = stint.Id.ToString(),
        Action = "MatchesUpdated",
        OldData = oldData,
        NewData = JsonSerializer.Serialize(new { stint.OfficialMatches, stint.EligibleSeasonIndex }),
    });
    database.ActivityEvents.Add(new ActivityEvent
    {
        ChallengeId = challengeId,
        ParticipantId = participant.Id,
        Type = "career.clubChanged",
        Message = $"Updated official match details for {stint.ClubName}.",
        ClubName = stint.ClubName,
        SeasonIndex = stint.EligibleSeasonIndex,
    });
    RaceScoringResult score;
    try
    {
        await database.SaveChangesAsync(cancellationToken);
        score = await recalculator.RecalculateAsync(challengeId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    catch (Exception exception) when (exception is DbUpdateConcurrencyException || IsSerializationFailure(exception))
    {
        logger.LogWarning(exception, "Concurrent club stint match update for challenge {ChallengeId}", challengeId);
        return Results.Conflict(new { title = "This club stint changed. Refresh it before saving your changes." });
    }
    await hub.Clients.Group($"challenge:{challengeId}")
        .SendAsync("career.clubChanged", new { challengeId, stintId }, cancellationToken);
    await hub.Clients.Group($"challenge:{challengeId}")
        .SendAsync("score.updated", new { challengeId, scores = score.Participants }, cancellationToken);
    await PublishUserUpdatesAsync(database, hub, challengeId, cancellationToken);
    return Results.Ok(new { stint.Id, stint.OfficialMatches, stint.EligibleSeasonIndex, stint.Version });
});

challenges.MapPut("/{challengeId:guid}/career/stints/{stintId:guid}", async (
    Guid challengeId, Guid stintId, CareerStintCreateRequest request, ClaimsPrincipal principal,
    ChallengeDbContext database, ScoreRecalculator recalculator, IHubContext<ChallengeHub> hub,
    ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    if (!request.Version.HasValue)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["version"] = ["A career stint version is required."] });
    return await ChangeCareerStintAsync(challengeId, stintId, request.Version.Value, request,
        principal, database, recalculator, hub, logger, cancellationToken);
});

challenges.MapDelete("/{challengeId:guid}/career/stints/{stintId:guid}", async (
    Guid challengeId, Guid stintId, long version, ClaimsPrincipal principal,
    ChallengeDbContext database, ScoreRecalculator recalculator, IHubContext<ChallengeHub> hub,
    ILogger<Program> logger, CancellationToken cancellationToken) =>
    await ChangeCareerStintAsync(challengeId, stintId, version, null,
        principal, database, recalculator, hub, logger, cancellationToken));

challenges.MapPost("/{challengeId:guid}/achievement-claims", async (
    Guid challengeId,
    ClaimCreateRequest request,
    ClaimsPrincipal principal,
    ChallengeDbContext database,
    ScoreRecalculator recalculator,
    IHubContext<ChallengeHub> hub,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    var userId = GetUserId(principal);
    await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
    var participant = await GetParticipantAsync(database, challengeId, userId, cancellationToken);
    if (participant is null) return Results.Forbid();
    var challenge = await database.Challenges.SingleAsync(item => item.Id == challengeId, cancellationToken);
    if (challenge.Status is ChallengeStatus.Draft or ChallengeStatus.Completed or ChallengeStatus.Archived)
        return Results.Conflict(new { title = "Gameplay is unavailable while this challenge is a draft or read-only." });
    var definition = await database.AchievementDefinitions.SingleOrDefaultAsync(
        item => item.Code == request.Code && (item.ChallengeId == null || item.ChallengeId == challengeId), cancellationToken);
    if (definition is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["code"] = ["Choose a valid achievement."] });
    if (RaceScoringEngine.AutomaticMilestones.Contains(request.Code) || request.Code == "G053")
        return Results.Conflict(new { title = "This milestone is calculated from career records." });
    if (request.Code == "G048" && await database.ScoreEvents.AnyAsync(item => item.ChallengeId == challengeId
            && item.ParticipantId == participant.Id && item.SourceType == "Achievement" && item.SourceKey == "automatic:G048", cancellationToken))
        return Results.Conflict(new { title = "This milestone is calculated from your recorded major trophies." });
    if (request.Code == "G033")
    {
        var claimCareer = await database.Careers.SingleAsync(item => item.ParticipantId == participant.Id, cancellationToken);
        var teams = await database.NationalTeamStints.Include(item => item.SeasonMatches)
            .Where(item => item.CareerId == claimCareer.Id).ToListAsync(cancellationToken);
        if (RaceScoringEngine.NationalTeamMilestone(claimCareer.NationalTeam20MatchSeasonIndex, teams).HasValue)
            return Results.Conflict(new { title = "This milestone is calculated from your national team career." });
    }
    var rule = await database.ChallengeRules
        .Where(item => item.ChallengeId == challengeId && item.AchievementCode == request.Code
            && item.EffectiveSeasonIndex <= request.SeasonIndex)
        .OrderByDescending(item => item.VersionNumber).FirstOrDefaultAsync(cancellationToken);
    if (rule is null || !rule.IsEnabled) return Results.Conflict(new { title = "This achievement is disabled for this challenge." });
    if (request.SeasonIndex < 0 || request.SeasonIndex > 100)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["seasonIndex"] = ["Choose a season index between 0 and 100."] });
    if (request.ClubName?.Length > 120 || request.CountryCode?.Length > 8 || request.Notes?.Length > 1000)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["claim"] = ["Achievement details are too long."] });
    var participationErrors = ValidateCompetitionParticipation(request.Code, request, rule.MinimumCupMatchesOverride ?? 2);
    if (participationErrors.Count > 0) return Results.ValidationProblem(participationErrors);
    var competitionError = await ValidateClaimCompetitionAsync(database, challengeId, request.Code, request.CompetitionId, cancellationToken);
    if (competitionError is not null)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["competitionId"] = [competitionError] });
    var clubError = await ValidateCatalogClubAsync(database, request.ClubId, request.ClearClubId,
        request.ClubName, request.CountryCode, cancellationToken);
    if (clubError is not null)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["clubId"] = [clubError] });
    if (request.Code == "G013")
    {
        var doubleError = await ValidateDomesticDoubleConsistencyAsync(database, participant.Id, null, request, cancellationToken);
        if (doubleError is not null)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["domesticDouble"] = [doubleError] });
    }
    if (request.Code == "G032")
    {
        var firstClubError = await ValidateCatalogClubAsync(database, request.FirstContinentalTitleClubId, request.ClearFirstContinentalTitleClubId,
            request.FirstContinentalTitleClub, request.FirstContinentalTitleCountry, cancellationToken);
        if (firstClubError is not null)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["firstContinentalTitleClubId"] = [firstClubError] });
    }
    if (request.Code is "G037" or "G041")
    {
        var startingError = await ValidateCatalogClubAsync(database, request.StartingCareerClubId, request.ClearStartingCareerClubId,
            request.StartingCareerClub, request.StartingCareerCountry, cancellationToken);
        if (startingError is not null)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["startingCareerClubId"] = [startingError] });
    }

    var career = await database.Careers.SingleAsync(item => item.ParticipantId == participant.Id, cancellationToken);
    if (request.Code == "G038")
    {
        var divisionError = await ValidateFirstDivisionExperienceAsync(database, participant.Id, career.Id, request.SeasonIndex, cancellationToken);
        if (divisionError is not null)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["firstDivisionExperience"] = [divisionError] });
    }
    if (request.Code == "G039")
    {
        var divisionError = await ValidateHigherDivisionMoveAsync(database, participant.Id, career.Id, request.SeasonIndex,
            request.PreviousBestDivisionLevel, cancellationToken);
        if (divisionError is not null)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["previousBestDivisionLevel"] = [divisionError] });
    }
    if (request.Code == "G050")
    {
        var continuityError = await ValidateThirdToFirstContinuityAsync(database, participant.Id, career.Id, request, cancellationToken);
        if (continuityError is not null)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["stayedWithClubFromThirdToFirst"] = [continuityError] });
    }
    if (request.Code == "G040")
    {
        var trophyError = await ValidateFirstMajorClubTrophyAsync(database, participant.Id, null, request, cancellationToken);
        if (trophyError is not null)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["firstMajorClubTrophy"] = [trophyError] });
    }
    if (request.Code is "G037" or "G041")
    {
        var moveError = await ValidateCareerMoveAsync(database, participant.Id, career.Id, null, request.Code, request, cancellationToken);
        if (moveError is not null)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["careerMove"] = [moveError] });
    }
    var existingClaims = await database.AchievementClaims
        .Where(item => item.ParticipantId == participant.Id && item.AchievementCode == request.Code)
        .ToListAsync(cancellationToken);
    var frequency = rule.FrequencyOverride ?? definition.Frequency;
    var newClaimId = Guid.NewGuid();
    var createdAt = DateTimeOffset.UtcNow;
    var candidate = new ClaimInput(newClaimId, participant.Id, request.Code, request.SeasonIndex,
        request.ClubName, request.CountryCode, createdAt, ClubId: request.ClubId);
    var duplicate = await HasFrequencyConflictAsync(database, challengeId, definition, existingClaims, candidate, cancellationToken);
    if (duplicate) return Results.Conflict(new { title = FrequencyConflictMessage(frequency) });
    if (request.Code == "G037" && existingClaims.Any(item => item.SeasonIndex == request.SeasonIndex))
        return Results.Conflict(new { title = "Only one club move can score in a season." });
    if (frequency is AchievementFrequency.PerClub or AchievementFrequency.ClubSeason && string.IsNullOrWhiteSpace(request.ClubName))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["clubName"] = ["A club is required for this achievement."] });
    if (frequency == AchievementFrequency.PerCountry && string.IsNullOrWhiteSpace(request.CountryCode))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["countryCode"] = ["A country is required for this achievement."] });
    if (await HasConflictingContinentalParticipationAsync(database, participant.Id, null, request.Code,
            request.ClubName, request.CountryCode, request.SeasonIndex, cancellationToken))
        return Results.Conflict(new { title = "The first and later continental participations for this club have conflicting seasons." });

    var claim = new AchievementClaim
    {
        Id = newClaimId,
        CareerId = career.Id,
        ParticipantId = participant.Id,
        AchievementCode = request.Code,
        OccurrenceKey = $"{GetOccurrenceKey(frequency, request.SeasonIndex, request.ClubName, request.CountryCode)}:claim:{newClaimId:N}",
        SeasonIndex = request.SeasonIndex,
        ClubId = request.ClubId,
        ClubName = request.ClubName?.Trim(),
        CountryCode = request.CountryCode?.Trim().ToUpperInvariant(),
        Notes = request.Notes?.Trim(),
        CreatedAt = createdAt,
    };
    SetCompetitionParticipation(claim, request);
    database.AchievementClaims.Add(claim);
    var activity = new ActivityEvent
    {
        ChallengeId = challengeId,
        ParticipantId = participant.Id,
        Type = "achievement.created",
        Message = $"{rule.NameOverride ?? definition.Name} recorded for season {challenge.StartSeasonYear + request.SeasonIndex}/{challenge.StartSeasonYear + request.SeasonIndex + 1}.",
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
        NewData = JsonSerializer.Serialize(claim),
    });
    await QueueOtherParticipantNotificationsAsync(
        database, challengeId, participant.Id, "achievement.created", "Your rival recorded an achievement",
        $"{career.TrainerName} recorded {rule.NameOverride ?? definition.Name} for season {challenge.StartSeasonYear + claim.SeasonIndex}/{(challenge.StartSeasonYear + claim.SeasonIndex + 1) % 100:00}.",
        claim.Id.ToString(), cancellationToken);
    RaceScoringResult score;
    try
    {
        await database.SaveChangesAsync(cancellationToken);
        score = await recalculator.RecalculateAsync(challengeId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    catch (Exception exception) when (exception is DbUpdateConcurrencyException || IsSerializationFailure(exception)
        || IsUniqueConstraintViolation(exception, "IX_AchievementClaims_ParticipantId_AchievementCode_OccurrenceK~"))
    {
        logger.LogWarning(exception, "Concurrent achievement creation for challenge {ChallengeId}", challengeId);
        return Results.Conflict(new { title = "The achievement or challenge changed. Refresh and try again." });
    }
    await hub.Clients.Group($"challenge:{challengeId}")
        .SendAsync("achievement.created", new { challengeId, claim.Id, code = claim.AchievementCode, seasonIndex = claim.SeasonIndex }, cancellationToken);
    await hub.Clients.Group($"challenge:{challengeId}")
        .SendAsync("score.updated", new { challengeId, scores = score.Participants }, cancellationToken);
    await hub.Clients.Group($"challenge:{challengeId}")
        .SendAsync("challenge.updated", new { challengeId }, cancellationToken);
    await PublishUserUpdatesAsync(database, hub, challengeId, cancellationToken);
    return Results.Created($"/api/challenges/{challengeId}/achievement-claims/{claim.Id}", new
    {
        claim.Id,
        claim.AchievementCode,
        claim.SeasonIndex,
        points = rule.Points,
    });
});

challenges.MapPut("/{challengeId:guid}/achievement-claims/{claimId:guid}", async (
    Guid challengeId, Guid claimId, ClaimUpdateRequest request, ClaimsPrincipal principal,
    ChallengeDbContext database, ScoreRecalculator recalculator, IHubContext<ChallengeHub> hub,
    ILogger<Program> logger, CancellationToken cancellationToken) =>
    await ChangeAchievementClaimAsync(challengeId, claimId, request.Version, request, principal,
        database, recalculator, hub, logger, cancellationToken));

challenges.MapDelete("/{challengeId:guid}/achievement-claims/{claimId:guid}", async (
    Guid challengeId, Guid claimId, long version, ClaimsPrincipal principal,
    ChallengeDbContext database, ScoreRecalculator recalculator, IHubContext<ChallengeHub> hub,
    ILogger<Program> logger, CancellationToken cancellationToken) =>
    await ChangeAchievementClaimAsync(challengeId, claimId, version, null, principal,
        database, recalculator, hub, logger, cancellationToken));

challenges.MapGet("/{challengeId:guid}/national-teams", async (
    Guid challengeId, ClaimsPrincipal principal, ChallengeDbContext database, CancellationToken cancellationToken) =>
{
    var participant = await GetParticipantAsync(database, challengeId, GetUserId(principal), cancellationToken);
    if (participant is null) return Results.Forbid();
    var careerId = await database.Careers.Where(item => item.ParticipantId == participant.Id)
        .Select(item => item.Id).SingleAsync(cancellationToken);
    var teams = await database.NationalTeamStints.Include(item => item.SeasonMatches).Where(item => item.CareerId == careerId)
        .OrderBy(item => item.StartSeasonIndex).ThenBy(item => item.TeamName).ToListAsync(cancellationToken);
    return Results.Ok(teams.Select(item => new
    {
        item.Id, item.TeamName, item.CountryCode, item.StartSeasonIndex, item.EndSeasonIndex, item.OfficialMatches, item.Notes, item.Version,
        seasonMatches = item.SeasonMatches.OrderBy(match => match.SeasonIndex).Select(match => new { match.SeasonIndex, match.OfficialMatches }),
    }));
});
challenges.MapPost("/{challengeId:guid}/national-teams", async (
    Guid challengeId, NationalTeamRequest request, ClaimsPrincipal principal, ChallengeDbContext database,
    ScoreRecalculator recalculator, IHubContext<ChallengeHub> hub, ILogger<Program> logger, CancellationToken cancellationToken) =>
    await ChangeNationalTeamAsync(challengeId, null, request.Version, request, principal, database, recalculator, hub, logger, cancellationToken));
challenges.MapPut("/{challengeId:guid}/national-teams/{stintId:guid}", async (
    Guid challengeId, Guid stintId, NationalTeamRequest request, ClaimsPrincipal principal, ChallengeDbContext database,
    ScoreRecalculator recalculator, IHubContext<ChallengeHub> hub, ILogger<Program> logger, CancellationToken cancellationToken) =>
    await ChangeNationalTeamAsync(challengeId, stintId, request.Version, request, principal, database, recalculator, hub, logger, cancellationToken));
challenges.MapDelete("/{challengeId:guid}/national-teams/{stintId:guid}", async (
    Guid challengeId, Guid stintId, long version, ClaimsPrincipal principal, ChallengeDbContext database,
    ScoreRecalculator recalculator, IHubContext<ChallengeHub> hub, ILogger<Program> logger, CancellationToken cancellationToken) =>
    await ChangeNationalTeamAsync(challengeId, stintId, version, null, principal, database, recalculator, hub, logger, cancellationToken));

challenges.MapGet("/{challengeId:guid}/transfers", async (
    Guid challengeId, ClaimsPrincipal principal, ChallengeDbContext database, CancellationToken cancellationToken) =>
{
    var participant = await GetParticipantAsync(database, challengeId, GetUserId(principal), cancellationToken);
    if (participant is null) return Results.Forbid();
    var careerId = await database.Careers.Where(item => item.ParticipantId == participant.Id)
        .Select(item => item.Id).SingleAsync(cancellationToken);
    var transfers = await database.Transfers.Where(item => item.CareerId == careerId)
        .OrderByDescending(item => item.SeasonIndex).ThenByDescending(item => item.CreatedAt)
        .ToListAsync(cancellationToken);
    return Results.Ok(transfers.Select(item => new
    {
        item.Id, item.SeasonIndex, item.ClubName, item.PlayerName, item.CounterpartyName,
        item.Direction, item.FixedAmount, item.Currency, item.Notes, item.Version,
    }));
});
challenges.MapPost("/{challengeId:guid}/transfers", async (
    Guid challengeId, TransferRequest request, ClaimsPrincipal principal, ChallengeDbContext database,
    ScoreRecalculator recalculator, IHubContext<ChallengeHub> hub, ILogger<Program> logger, CancellationToken cancellationToken) =>
    await ChangeTransferAsync(challengeId, null, request.Version, request, principal, database, recalculator, hub, logger, cancellationToken));
challenges.MapPut("/{challengeId:guid}/transfers/{transferId:guid}", async (
    Guid challengeId, Guid transferId, TransferRequest request, ClaimsPrincipal principal, ChallengeDbContext database,
    ScoreRecalculator recalculator, IHubContext<ChallengeHub> hub, ILogger<Program> logger, CancellationToken cancellationToken) =>
    await ChangeTransferAsync(challengeId, transferId, request.Version, request, principal, database, recalculator, hub, logger, cancellationToken));
challenges.MapDelete("/{challengeId:guid}/transfers/{transferId:guid}", async (
    Guid challengeId, Guid transferId, long version, ClaimsPrincipal principal, ChallengeDbContext database,
    ScoreRecalculator recalculator, IHubContext<ChallengeHub> hub, ILogger<Program> logger, CancellationToken cancellationToken) =>
    await ChangeTransferAsync(challengeId, transferId, version, null, principal, database, recalculator, hub, logger, cancellationToken));

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
    var seasonIds = seasons.Select(item => item.Id).ToArray();
    var clubStatistics = await database.ClubSeasonStatistics.Where(item => seasonIds.Contains(item.CareerSeasonId))
        .ToListAsync(cancellationToken);
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
        item.UsesClubBreakdown,
        clubSeasons = clubStatistics.Where(row => row.CareerSeasonId == item.Id).Select(row => new
        {
            row.CareerStintId, row.DivisionLevel, row.OfficialMatches, row.LeagueMatches, row.LeagueWins,
            row.LeagueDraws, row.LeagueLosses, row.LeaguePoints, row.GoalsFor, row.GoalsAgainst, row.LeaguePosition,
        }),
        item.LargestOutgoingTransfer,
        item.Currency,
        item.Notes,
        item.Version,
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
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
    var participant = await GetParticipantAsync(database, challengeId, GetUserId(principal), cancellationToken);
    if (participant is null) return Results.Forbid();
    var challenge = await database.Challenges.SingleAsync(item => item.Id == challengeId, cancellationToken);
    if (challenge.Status is ChallengeStatus.Draft or ChallengeStatus.Completed or ChallengeStatus.Archived)
        return Results.Conflict(new { title = "Gameplay is unavailable while this challenge is a draft or read-only." });
    if (seasonIndex is < 0 or > 100 || request.LeagueMatches < 0
        || request.LeagueWins < 0 || request.LeagueDraws < 0 || request.LeagueLosses < 0
        || request.LeaguePoints < 0 || request.GoalsFor < 0 || request.GoalsAgainst < 0
        || request.LargestOutgoingTransfer < 0 || request.LargestOutgoingTransfer > 9999999999999.99m
        || decimal.Round(request.LargestOutgoingTransfer, 2) != request.LargestOutgoingTransfer
        || (long)request.LeagueWins + request.LeagueDraws + request.LeagueLosses != request.LeagueMatches
        || request.LeaguePosition is < 1 or > 100
        || request.Currency is null || request.Currency.Trim().Length != 3
        || !request.Currency.Trim().All(char.IsAsciiLetter)
        || request.Notes?.Length > 1000)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["season"] = ["Enter valid non-negative statistics, matching win/draw/loss totals, a league position from 1 to 100, and a three-letter currency code."] });

    var career = await database.Careers.SingleAsync(item => item.ParticipantId == participant.Id, cancellationToken);
    var season = await database.CareerSeasons.SingleOrDefaultAsync(
        item => item.CareerId == career.Id && item.SeasonIndex == seasonIndex, cancellationToken);
    if ((season is null && request.Version is not null) || (season is not null && request.Version != season.Version))
        return Results.Conflict(new { title = "This season changed. Refresh it before saving your changes." });
    if (season?.UsesClubBreakdown == true && request.UsesClubBreakdown is null)
        return Results.Conflict(new { title = "This season uses club statistics. Refresh using a client that supports club breakdowns." });
    var useClubBreakdown = request.UsesClubBreakdown ?? false;
    var existingClubStatistics = season is null ? [] : await database.ClubSeasonStatistics
        .Where(item => item.CareerSeasonId == season.Id).ToListAsync(cancellationToken);
    if (!useClubBreakdown && request.ClubSeasons is not null)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["clubSeasons"] = ["Club statistics can only be changed when the club breakdown is selected."] });
    if (useClubBreakdown && request.ClearClubBreakdown)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["clubSeasons"] = ["Switch to the quick summary before clearing club statistics."] });
    if (useClubBreakdown)
    {
        if (request.ClubSeasons is not { Length: > 0 and <= 100 }
            || request.ClubSeasons.Select(item => item.CareerStintId).Distinct().Count() != request.ClubSeasons.Length
            || request.ClubSeasons.Any(item => item.DivisionLevel is < 1 or > 100 || item.OfficialMatches is < 0 or > 5000
                || item.LeagueMatches < 0 || item.LeagueMatches > item.OfficialMatches
                || item.LeagueWins < 0 || item.LeagueDraws < 0 || item.LeagueLosses < 0
                || (long)item.LeagueWins + item.LeagueDraws + item.LeagueLosses != item.LeagueMatches
                || item.LeaguePoints < 0 || item.GoalsFor < 0 || item.GoalsAgainst < 0
                || item.LeaguePosition is < 1 or > 100)
            || request.ClubSeasons.Sum(item => (long)item.LeagueMatches) > int.MaxValue
            || request.ClubSeasons.Sum(item => (long)item.LeaguePoints) > int.MaxValue
            || request.ClubSeasons.Sum(item => (long)item.GoalsFor) > int.MaxValue
            || request.ClubSeasons.Sum(item => (long)item.GoalsAgainst) > int.MaxValue)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["clubSeasons"] = ["Enter valid, distinct club statistics with matching league results."] });
        var stintIds = request.ClubSeasons.Select(item => item.CareerStintId).ToArray();
        var stints = await database.CareerStints.Where(item => item.CareerId == career.Id && stintIds.Contains(item.Id))
            .ToListAsync(cancellationToken);
        var currentSeasonId = season?.Id;
        var otherStatistics = await database.ClubSeasonStatistics
            .Where(item => stintIds.Contains(item.CareerStintId) && (!currentSeasonId.HasValue || item.CareerSeasonId != currentSeasonId.Value))
            .ToListAsync(cancellationToken);
        if (stints.Count != stintIds.Length || request.ClubSeasons.Any(row =>
        {
            var stint = stints.SingleOrDefault(item => item.Id == row.CareerStintId);
            return stint is null || seasonIndex < stint.StartSeasonIndex || seasonIndex > (stint.EndSeasonIndex ?? 100)
                || (long)row.OfficialMatches + otherStatistics.Where(item => item.CareerStintId == row.CareerStintId)
                    .Sum(item => (long)item.OfficialMatches) > stint.OfficialMatches;
        }))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["clubSeasons"] = ["Choose your own club stints active in this season; their season matches cannot exceed the recorded stint totals."] });
    }
    var changedCurrency = request.Currency.Trim().ToUpperInvariant();
    var transfersToRelabel = await database.Transfers.Where(item => item.CareerId == career.Id
        && item.SeasonIndex == seasonIndex && item.Currency != changedCurrency).ToListAsync(cancellationToken);
    if (transfersToRelabel.Count != 0 && !request.ConfirmTransferCurrencyRelabel)
        return Results.Conflict(new { title = "Confirm relabelling the season's transfer currencies before changing them. Amounts will not be converted." });
    foreach (var transfer in transfersToRelabel)
    {
        var oldCurrency = transfer.Currency;
        transfer.Currency = changedCurrency;
        transfer.Version++;
        database.AuditLogs.Add(new AuditLog
        {
            ActorId = GetUserId(principal), ChallengeId = challengeId, EntityType = "Transfer",
            EntityId = transfer.Id.ToString(), Action = "CurrencyRelabelled",
            OldData = JsonSerializer.Serialize(new { Currency = oldCurrency }),
            NewData = JsonSerializer.Serialize(new { transfer.Currency }),
        });
    }
    var wasCompleted = season?.Status == SeasonStatus.Completed;
    var oldData = season is null ? null : JsonSerializer.Serialize(new { Season = season, ClubSeasons = existingClubStatistics });
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
    season.UsesClubBreakdown = useClubBreakdown;
    if (request.ClearClubBreakdown) database.ClubSeasonStatistics.RemoveRange(existingClubStatistics);
    if (useClubBreakdown)
    {
        var rows = request.ClubSeasons!;
        season.LeagueMatches = (int)rows.Sum(item => (long)item.LeagueMatches);
        season.LeagueWins = (int)rows.Sum(item => (long)item.LeagueWins);
        season.LeagueDraws = (int)rows.Sum(item => (long)item.LeagueDraws);
        season.LeagueLosses = (int)rows.Sum(item => (long)item.LeagueLosses);
        season.LeaguePoints = (int)rows.Sum(item => (long)item.LeaguePoints);
        season.GoalsFor = (int)rows.Sum(item => (long)item.GoalsFor);
        season.GoalsAgainst = (int)rows.Sum(item => (long)item.GoalsAgainst);
        season.LeaguePosition = rows.Length == 1 ? rows[0].LeaguePosition : null;
        database.ClubSeasonStatistics.RemoveRange(existingClubStatistics.Where(item => !rows.Any(row => row.CareerStintId == item.CareerStintId)));
        foreach (var row in rows)
        {
            var entry = existingClubStatistics.SingleOrDefault(item => item.CareerStintId == row.CareerStintId);
            if (entry is null)
            {
                entry = new ClubSeasonStatistics { CareerSeasonId = season.Id, CareerStintId = row.CareerStintId };
                database.ClubSeasonStatistics.Add(entry);
            }
            entry.DivisionLevel = row.DivisionLevel;
            entry.OfficialMatches = row.OfficialMatches;
            entry.LeagueMatches = row.LeagueMatches;
            entry.LeagueWins = row.LeagueWins;
            entry.LeagueDraws = row.LeagueDraws;
            entry.LeagueLosses = row.LeagueLosses;
            entry.LeaguePoints = row.LeaguePoints;
            entry.GoalsFor = row.GoalsFor;
            entry.GoalsAgainst = row.GoalsAgainst;
            entry.LeaguePosition = row.LeaguePosition;
        }
    }
    season.LargestOutgoingTransfer = request.LargestOutgoingTransfer;
    season.Currency = changedCurrency;
    season.Notes = request.Notes?.Trim();
    season.Status = request.IsCompleted ? SeasonStatus.Completed : SeasonStatus.InProgress;
    season.CompletedAt = request.IsCompleted ? season.CompletedAt ?? DateTimeOffset.UtcNow : null;
    season.Version++;
    var eventType = request.IsCompleted && !wasCompleted ? "season.completed"
        : !request.IsCompleted && wasCompleted ? "season.reopened" : "season.updated";
    database.AuditLogs.Add(new AuditLog
    {
        ActorId = GetUserId(principal),
        ChallengeId = challengeId,
        EntityType = "CareerSeason",
        EntityId = season.Id.ToString(),
        Action = eventType,
        OldData = oldData,
        NewData = JsonSerializer.Serialize(new { Season = season, ClubSeasons = useClubBreakdown ? request.ClubSeasons : null, request.ClearClubBreakdown }),
    });
    database.ActivityEvents.Add(new ActivityEvent
    {
        ChallengeId = challengeId,
        ParticipantId = participant.Id,
        Type = eventType,
        Message = eventType == "season.completed" ? $"Completed {season.DisplayName}."
            : eventType == "season.reopened" ? $"Reopened {season.DisplayName}."
            : $"Updated {season.DisplayName}.",
        SeasonIndex = seasonIndex,
    });
    RaceScoringResult score;
    try
    {
        await database.SaveChangesAsync(cancellationToken);
        score = await recalculator.RecalculateAsync(challengeId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    catch (Exception exception) when (exception is DbUpdateConcurrencyException || IsSerializationFailure(exception) || IsDuplicateSeason(exception))
    {
        logger.LogWarning(exception, "Concurrent season update for challenge {ChallengeId}, season {SeasonIndex}", challengeId, seasonIndex);
        return Results.Conflict(new { title = "This season changed. Refresh it before saving your changes." });
    }
    await hub.Clients.Group($"challenge:{challengeId}")
        .SendAsync(eventType, new { challengeId, seasonIndex }, cancellationToken);
    await hub.Clients.Group($"challenge:{challengeId}")
        .SendAsync("score.updated", new { challengeId, scores = score.Participants }, cancellationToken);
    await hub.Clients.Group($"challenge:{challengeId}")
        .SendAsync("challenge.updated", new { challengeId }, cancellationToken);
    await PublishUserUpdatesAsync(database, hub, challengeId, cancellationToken);
    return Results.Ok(new { season.Id, season.SeasonIndex, Status = season.Status.ToString(), season.Version });
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
    await DatabaseMigrations.ApplyAsync(database);
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

static async Task QueueOtherParticipantNotificationsAsync(
    ChallengeDbContext database,
    Guid challengeId,
    Guid actorParticipantId,
    string type,
    string title,
    string message,
    string? relatedEntityId,
    CancellationToken cancellationToken)
{
    var recipients = await database.ChallengeParticipants
        .Where(item => item.ChallengeId == challengeId && item.Id != actorParticipantId)
        .Select(item => item.UserId)
        .ToArrayAsync(cancellationToken);
    database.UserNotifications.AddRange(recipients.Select(userId => new UserNotification
    {
        UserId = userId,
        ChallengeId = challengeId,
        Type = type,
        Title = title,
        Message = message,
        RelatedEntityId = relatedEntityId,
    }));
}

static async Task PublishUserUpdatesAsync(
    ChallengeDbContext database,
    IHubContext<ChallengeHub> hub,
    Guid challengeId,
    CancellationToken cancellationToken)
{
    var recipients = await database.ChallengeParticipants.AsNoTracking()
        .Where(item => item.ChallengeId == challengeId)
        .Select(item => item.UserId)
        .ToArrayAsync(cancellationToken);
    foreach (var recipient in recipients)
        await hub.Clients.Group($"user:{recipient}")
            .SendAsync("notification.created", new { challengeId }, cancellationToken);
}

static async Task<(IResult? Error, Guid? ParticipantId)> JoinChallengeAsync(
    ChallengeInvitation invitation,
    ApplicationUser user,
    ChallengeDbContext database,
    ScoreRecalculator recalculator,
    ILogger<Program> logger,
    CancellationToken cancellationToken)
{
    try
    {
        return await JoinChallengeCoreAsync(invitation, user, database, recalculator, cancellationToken);
    }
    catch (Exception exception) when (IsSerializationFailure(exception))
    {
        logger.LogWarning(exception, "Concurrent invitation acceptance for challenge {ChallengeId}", invitation.ChallengeId);
        return (Results.Conflict(new { title = "The challenge changed while accepting the invitation. Please try again." }), null);
    }
}

static bool IsSerializationFailure(Exception exception)
{
    for (Exception? current = exception; current is not null; current = current.InnerException)
        if (current is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure }) return true;
    return false;
}

static bool IsDuplicateSeason(Exception exception)
    => IsUniqueConstraintViolation(exception, "IX_CareerSeasons_CareerId_SeasonIndex");

static bool IsUniqueConstraintViolation(Exception exception, string constraintName)
{
    for (Exception? current = exception; current is not null; current = current.InnerException)
        if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres
            && postgres.ConstraintName == constraintName) return true;
    return false;
}

static async Task<(IResult? Error, Guid? ParticipantId)> JoinChallengeCoreAsync(
    ChallengeInvitation invitation,
    ApplicationUser user,
    ChallengeDbContext database,
    ScoreRecalculator recalculator,
    CancellationToken cancellationToken)
{
    await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
    await database.Entry(invitation).ReloadAsync(cancellationToken);
    if (database.Entry(invitation).State == EntityState.Detached)
        return (Results.NotFound(new { title = "This invitation no longer exists." }), null);
    if (invitation.RevokedAt.HasValue || invitation.RedeemedAt.HasValue || invitation.DeclinedAt.HasValue
        || invitation.ExpiresAt <= DateTimeOffset.UtcNow)
        return (Results.Conflict(new { title = "This invitation has expired or has already been used." }), null);
    if (invitation.InviteeEmail is not null && !invitation.InviteeEmail.Equals(user.Email, StringComparison.OrdinalIgnoreCase))
        return (Results.Forbid(), null);

    var challenge = await database.Challenges.SingleOrDefaultAsync(
        item => item.Id == invitation.ChallengeId, cancellationToken);
    if (challenge is null || challenge.Status != ChallengeStatus.PendingPlayers)
        return (Results.Conflict(new { title = "This challenge is not accepting players." }), null);
    if (await database.ChallengeParticipants.AnyAsync(
            item => item.ChallengeId == challenge.Id && item.UserId == user.Id, cancellationToken))
        return (Results.Conflict(new { title = "You are already a participant in this challenge." }), null);
    if (await database.ChallengeParticipants.CountAsync(item => item.ChallengeId == challenge.Id, cancellationToken) >= 2)
        return (Results.Conflict(new { title = "This challenge already has two participants." }), null);

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
        RequiresStartingStatus = true,
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

static async Task<IResult> ChangeArchiveAsync(
    Guid challengeId, ChallengeCorrectionRequest request, bool archive, ClaimsPrincipal principal,
    ChallengeDbContext database, IHubContext<ChallengeHub> hub, ILogger<Program> logger, CancellationToken cancellationToken)
{
    await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
    var userId = GetUserId(principal);
    var participant = await GetParticipantAsync(database, challengeId, userId, cancellationToken);
    if (participant is null || participant.Role != ParticipantRole.Administrator) return Results.Forbid();
    if (!request.Confirm)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["confirm"] = ["Confirm the change to the challenge status."] });
    var challenge = await database.Challenges.SingleAsync(item => item.Id == challengeId, cancellationToken);
    if (archive && (challenge.Status == ChallengeStatus.Archived || challenge.IsCorrectionMode))
        return Results.Conflict(new { title = "This challenge is already archived or has unfinished corrections." });
    if (!archive && (challenge.Status != ChallengeStatus.Archived || challenge.StatusBeforeArchive is null
        || challenge.StatusBeforeArchive == ChallengeStatus.Archived))
        return Results.Conflict(new { title = "This challenge cannot be restored." });
    var priorStatus = challenge.Status;
    var revokedRecipients = new List<Guid>();
    if (archive)
    {
        challenge.StatusBeforeArchive = challenge.Status;
        challenge.Status = ChallengeStatus.Archived;
        var invitations = await database.ChallengeInvitations.Where(item => item.ChallengeId == challengeId
            && item.RedeemedAt == null && item.RevokedAt == null && item.DeclinedAt == null
            && item.ExpiresAt > DateTimeOffset.UtcNow).ToListAsync(cancellationToken);
        foreach (var invitation in invitations)
        {
            invitation.RevokedAt = DateTimeOffset.UtcNow;
            database.AuditLogs.Add(new AuditLog
            {
                ActorId = userId, ChallengeId = challengeId, EntityType = "ChallengeInvitation",
                EntityId = invitation.Id.ToString(), Action = "Revoked",
                NewData = JsonSerializer.Serialize(new { Reason = "Challenge archived" }),
            });
        }
        var emails = invitations.Where(item => item.InviteeEmail != null).Select(item => item.InviteeEmail!).Distinct().ToArray();
        var users = await database.Users.Where(item => item.NormalizedEmail != null && emails.Contains(item.NormalizedEmail))
            .Select(item => item.Id).ToArrayAsync(cancellationToken);
        foreach (var recipientId in users)
        {
            revokedRecipients.Add(recipientId);
            database.UserNotifications.Add(new UserNotification
            {
                UserId = recipientId, ChallengeId = challengeId, Type = "challenge.invitation.revoked",
                Title = "An invitation was revoked", Message = "An invitation is no longer available because the challenge was archived.",
                RelatedEntityId = challengeId.ToString(),
            });
        }
    }
    else
    {
        challenge.Status = challenge.StatusBeforeArchive!.Value;
        challenge.StatusBeforeArchive = null;
    }
    challenge.Version++;
    var eventType = archive ? "challenge.archived" : "challenge.restored";
    database.AuditLogs.Add(new AuditLog
    {
        ActorId = userId, ChallengeId = challengeId, EntityType = "Challenge", EntityId = challengeId.ToString(),
        Action = eventType, OldData = JsonSerializer.Serialize(new { Status = priorStatus }),
        NewData = JsonSerializer.Serialize(new { challenge.Status }),
    });
    database.ActivityEvents.Add(new ActivityEvent
    {
        ChallengeId = challengeId, ParticipantId = participant.Id, Type = eventType,
        Message = archive ? "The challenge was archived." : "The challenge was restored.",
    });
    await QueueOtherParticipantNotificationsAsync(database, challengeId, participant.Id, eventType,
        archive ? "Challenge archived" : "Challenge restored",
        archive ? "Your challenge was archived." : "Your challenge was restored.", challengeId.ToString(), cancellationToken);
    try
    {
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    catch (Exception exception) when (exception is DbUpdateConcurrencyException || IsSerializationFailure(exception))
    {
        logger.LogWarning(exception, "Concurrent archive change for challenge {ChallengeId}", challengeId);
        return Results.Conflict(new { title = "The challenge changed. Refresh and try again." });
    }
    await hub.Clients.Group($"challenge:{challengeId}").SendAsync("challenge.updated", new { challengeId }, cancellationToken);
    foreach (var recipient in revokedRecipients)
        await hub.Clients.Group($"user:{recipient}").SendAsync("challenge.invitationReceived", new { challengeId }, cancellationToken);
    await PublishUserUpdatesAsync(database, hub, challengeId, cancellationToken);
    return Results.Ok(new { Status = challenge.Status.ToString() });
}

static async Task<IResult> ChangeRulesAsync(
    Guid challengeId, RuleChangeRequest request, bool preview, ClaimsPrincipal principal,
    ChallengeDbContext database, ScoreRecalculator recalculator, IHubContext<ChallengeHub>? hub,
    ILogger<Program>? logger, CancellationToken cancellationToken)
{
    await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
    var userId = GetUserId(principal);
    var participant = await GetParticipantAsync(database, challengeId, userId, cancellationToken);
    if (participant is null || participant.Role != ParticipantRole.Administrator) return Results.Forbid();
    var challenge = await database.Challenges.SingleAsync(item => item.Id == challengeId, cancellationToken);
    if (challenge.Status is ChallengeStatus.Completed or ChallengeStatus.Archived)
        return Results.Conflict(new { title = "This challenge is read-only." });
    if (request.Code is null || !RaceScoringEngine.SupplementalRuleDefaults.ContainsKey(request.Code)
        && !await database.AchievementDefinitions.AnyAsync(
            item => item.Code == request.Code && (item.ChallengeId == null || item.ChallengeId == challengeId), cancellationToken)
        || request.Points is < 0 or > 10000 || request.Policy is not ("Retroactive" or "FromSeason")
        || request.EffectiveSeasonIndex is < 0 or > 100 || request.Reason?.Length > 1000)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["rules"] = ["Choose a valid scoring rule, points, policy, and season."] });
    if (new[] { request.NameOverride, request.DescriptionOverride, request.CategoryOverride }
            .Any(value => value is not null && string.IsNullOrWhiteSpace(value))
        || request.NameOverride?.Trim().Length > 120 || request.DescriptionOverride?.Trim().Length > 500
        || request.CategoryOverride?.Trim().Length > 80
        || RaceScoringEngine.SupplementalRuleDefaults.ContainsKey(request.Code)
            && (request.NameOverride is not null || request.DescriptionOverride is not null || request.CategoryOverride is not null))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["achievement"] = ["Enter valid achievement text for an achievement rule."] });
    AchievementFrequency? frequencyOverride = null;
    if (request.FrequencyOverride is not null)
    {
        if (!CanChangeAchievementFrequency(request.Code)
            || !Enum.TryParse<AchievementFrequency>(request.FrequencyOverride, false, out var frequency)
            || !Enum.IsDefined(frequency))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["frequency"] = ["Choose a supported frequency for a manual achievement."] });
        frequencyOverride = frequency;
    }
    if (request.MinimumCupMatchesOverride is not null
        && (request.Code is not ("G008" or "G009" or "G015") || request.MinimumCupMatchesOverride is < 1 or > 1000))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["minimumCupMatches"] = ["Choose a cup minimum between 1 and 1000 for a supported achievement."] });
    if (request.ExpectedChallengeVersion != challenge.Version)
        return Results.Conflict(new { title = "The challenge changed. Refresh and preview the rule impact again." });
    if (!preview && !request.Confirm)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["confirm"] = ["Confirm the rule change and its impact."] });
    var input = await recalculator.GetInputAsync(challengeId, cancellationToken);
    var priorVersions = input.RuleVersions ?? [];
    var current = priorVersions.OrderByDescending(item => item.VersionNumber).FirstOrDefault();
    if (current is null) return Results.Problem("No rules are configured for this challenge.", statusCode: 500);
    if (current.VersionNumber != request.ExpectedVersion)
        return Results.Conflict(new { title = "The rules changed. Refresh the rulebook before applying your changes." });
    if (!current.Rules.ContainsKey(request.Code))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["rules"] = ["This achievement is not part of the challenge."] });
    var effectiveSeason = request.Policy == "Retroactive" ? 0 : request.EffectiveSeasonIndex;
    var nextVersions = CreateRuleVersions(priorVersions, effectiveSeason, request.Code,
        new RuleSetting(request.Points, request.IsEnabled, request.NameOverride?.Trim(),
            request.DescriptionOverride?.Trim(), request.CategoryOverride?.Trim(), frequencyOverride, request.MinimumCupMatchesOverride));
    var next = nextVersions[^1];
    var before = RaceScoringEngine.Calculate(input);
    var after = RaceScoringEngine.Calculate(input with { RuleVersions = [.. priorVersions, .. nextVersions] });
    var impact = after.Participants.Select(item =>
    {
        var old = before.Participants.Single(previous => previous.ParticipantId == item.ParticipantId);
        return new
        {
            participantId = item.ParticipantId,
            beforeConfirmed = old.ConfirmedPoints,
            afterConfirmed = item.ConfirmedPoints,
            beforePotential = old.PotentialPoints,
            afterPotential = item.PotentialPoints,
        };
    }).ToArray();
    if (preview) return Results.Ok(new { version = next.VersionNumber, effectiveSeasonIndex = effectiveSeason, impact,
        beforeWinner = before.WinningParticipantId, afterWinner = after.WinningParticipantId });

    database.ChallengeRules.AddRange(nextVersions.SelectMany(version => version.Rules.Select(rule => new ChallengeRule
    {
        ChallengeId = challengeId, AchievementCode = rule.Key, VersionNumber = version.VersionNumber,
        EffectiveSeasonIndex = version.EffectiveSeasonIndex, Points = rule.Value.Points, IsEnabled = rule.Value.IsEnabled,
        NameOverride = rule.Value.NameOverride, DescriptionOverride = rule.Value.DescriptionOverride,
        CategoryOverride = rule.Value.CategoryOverride, FrequencyOverride = rule.Value.FrequencyOverride,
        MinimumCupMatchesOverride = rule.Value.MinimumCupMatchesOverride,
    })));
    database.AuditLogs.Add(new AuditLog
    {
        ActorId = userId, ChallengeId = challengeId, EntityType = "ChallengeRules",
        EntityId = next.VersionNumber.ToString(), Action = "VersionCreated",
        OldData = JsonSerializer.Serialize(current),
        NewData = JsonSerializer.Serialize(new { Versions = nextVersions, request.Policy, request.Reason, request.Code, request.Points, request.IsEnabled }),
    });
    database.ActivityEvents.Add(new ActivityEvent
    {
        ChallengeId = challengeId, ParticipantId = participant.Id, Type = "challenge.rulesUpdated",
        Message = $"Updated the challenge rules to version {next.VersionNumber}.",
    });
    await QueueOtherParticipantNotificationsAsync(database, challengeId, participant.Id, "challenge.rulesUpdated",
        "Challenge rules updated", $"The challenge rules were updated to version {next.VersionNumber}.",
        next.VersionNumber.ToString(), cancellationToken);
    RaceScoringResult score;
    try
    {
        await database.SaveChangesAsync(cancellationToken);
        score = await recalculator.RecalculateAsync(challengeId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    catch (Exception exception) when (exception is DbUpdateConcurrencyException || IsSerializationFailure(exception)
        || IsUniqueConstraintViolation(exception, "PK_ChallengeRules"))
    {
        logger?.LogWarning(exception, "Concurrent rule change for challenge {ChallengeId}", challengeId);
        return Results.Conflict(new { title = "The rules changed. Refresh the rulebook before applying your changes." });
    }
    if (hub is null) throw new InvalidOperationException("The challenge update hub is not available.");
    await hub.Clients.Group($"challenge:{challengeId}").SendAsync("challenge.rulesUpdated", new { challengeId, version = next.VersionNumber }, cancellationToken);
    await hub.Clients.Group($"challenge:{challengeId}").SendAsync("score.updated", new { challengeId, scores = score.Participants }, cancellationToken);
    await hub.Clients.Group($"challenge:{challengeId}").SendAsync("challenge.updated", new { challengeId }, cancellationToken);
    await PublishUserUpdatesAsync(database, hub, challengeId, cancellationToken);
    return Results.Ok(new { version = next.VersionNumber, effectiveSeasonIndex = effectiveSeason, impact,
        beforeWinner = before.WinningParticipantId, afterWinner = after.WinningParticipantId });
}

static RuleVersionInput[] CreateRuleVersions(
    IReadOnlyList<RuleVersionInput> priorVersions, int effectiveSeason, string code, RuleSetting setting)
{
    var boundaries = priorVersions.Select(item => item.EffectiveSeasonIndex).Where(index => index > effectiveSeason)
        .Append(effectiveSeason).Distinct().Order().ToArray();
    var versionNumber = priorVersions.Max(item => item.VersionNumber);
    return boundaries.Select(boundary =>
    {
        var source = priorVersions.Where(item => item.EffectiveSeasonIndex <= boundary)
            .OrderByDescending(item => item.VersionNumber).First();
        var rules = source.Rules.ToDictionary(item => item.Key, item => item.Value);
        rules.TryGetValue(code, out var previous);
        rules[code] = setting with
        {
            NameOverride = setting.NameOverride ?? previous?.NameOverride,
            DescriptionOverride = setting.DescriptionOverride ?? previous?.DescriptionOverride,
            CategoryOverride = setting.CategoryOverride ?? previous?.CategoryOverride,
            FrequencyOverride = setting.FrequencyOverride ?? previous?.FrequencyOverride,
            MinimumCupMatchesOverride = setting.MinimumCupMatchesOverride ?? previous?.MinimumCupMatchesOverride,
        };
        return new RuleVersionInput(++versionNumber, boundary, rules);
    }).ToArray();
}

static bool CanChangeAchievementFrequency(string code) =>
    !RaceScoringEngine.SupplementalRuleDefaults.ContainsKey(code)
    && !RaceScoringEngine.AutomaticMilestones.Contains(code)
    && code is not ("G033" or "G037" or "G048" or "G053");

static async Task<bool> HasFrequencyConflictAsync(
    ChallengeDbContext database, Guid challengeId, AchievementDefinition definition,
    IReadOnlyList<AchievementClaim> existingClaims, ClaimInput candidate, CancellationToken cancellationToken)
{
    var claimsToCheck = existingClaims.Select(item => (item.ClubName, item.CountryCode))
        .Append((candidate.ClubName, candidate.CountryCode)).ToArray();
    var countries = claimsToCheck.Where(item => !string.IsNullOrWhiteSpace(item.CountryCode))
        .Select(item => item.CountryCode!.Trim().ToUpperInvariant()).Distinct().ToArray();
    var names = claimsToCheck.Where(item => !string.IsNullOrWhiteSpace(item.ClubName))
        .Select(item => item.ClubName!.Trim().ToUpperInvariant()).Distinct().ToArray();
    var clubEntries = await database.Clubs.AsNoTracking()
        .Where(item => countries.Contains(item.CountryCode) && names.Contains(item.NormalizedName))
        .Select(item => new { item.Id, item.CountryCode, item.NormalizedName })
        .ToListAsync(cancellationToken);
    var uniqueClubIds = clubEntries.GroupBy(item => (item.CountryCode, item.NormalizedName))
        .Where(group => group.Select(item => item.Id).Distinct().Count() == 1)
        .ToDictionary(group => group.Key, group => group.Single().Id);
    var ambiguousClubIdentities = clubEntries.GroupBy(item => (item.CountryCode, item.NormalizedName))
        .Where(group => group.Select(item => item.Id).Distinct().Count() > 1)
        .Select(group => group.Key)
        .ToHashSet();
    ClaimInput ResolveUniqueClub(ClaimInput claim) =>
        claim.ClubId.HasValue || string.IsNullOrWhiteSpace(claim.CountryCode) || string.IsNullOrWhiteSpace(claim.ClubName)
            ? claim
            : uniqueClubIds.TryGetValue((claim.CountryCode.Trim().ToUpperInvariant(),
                claim.ClubName.Trim().ToUpperInvariant()), out var clubId) ? claim with { ClubId = clubId }
            : ambiguousClubIdentities.Contains((claim.CountryCode.Trim().ToUpperInvariant(),
                claim.ClubName.Trim().ToUpperInvariant())) ? claim with { ClubIdentityAmbiguous = true } : claim;
    candidate = ResolveUniqueClub(candidate);
    var versions = await database.ChallengeRules.Where(item => item.ChallengeId == challengeId
        && item.AchievementCode == definition.Code).ToListAsync(cancellationToken);
    AchievementFrequency FrequencyAt(int season) => versions.Where(item => item.EffectiveSeasonIndex <= season)
        .OrderByDescending(item => item.VersionNumber).FirstOrDefault()?.FrequencyOverride ?? definition.Frequency;
    foreach (var claim in existingClaims)
    {
        var claimRule = versions.Where(item => item.EffectiveSeasonIndex <= claim.SeasonIndex)
            .OrderByDescending(item => item.VersionNumber).FirstOrDefault();
        if (claimRule is null || !claimRule.IsEnabled) continue;
        if (claim.AchievementCode is "G008" or "G009" or "G015"
            && claim.ManagedCompetitionMatches is int managed
            && managed < (claimRule?.MinimumCupMatchesOverride ?? 2)) continue;
        var existing = ResolveUniqueClub(new ClaimInput(claim.Id, claim.ParticipantId, claim.AchievementCode, claim.SeasonIndex,
            claim.ClubName, claim.CountryCode, claim.CreatedAt, ClubId: claim.ClubId));
        var existingFirst = existing.SeasonIndex < candidate.SeasonIndex
            || existing.SeasonIndex == candidate.SeasonIndex && (existing.CreatedAt < candidate.CreatedAt
                || existing.CreatedAt == candidate.CreatedAt && existing.Id.CompareTo(candidate.Id) < 0);
        var earlier = existingFirst ? existing : candidate;
        var later = existingFirst ? candidate : existing;
        if (RaceScoringEngine.FrequencyMatches(FrequencyAt(later.SeasonIndex), earlier, later)) return true;
    }
    return false;
}

static async Task<IResult> ChangeNationalTeamAsync(
    Guid challengeId, Guid? stintId, long? version, NationalTeamRequest? request,
    ClaimsPrincipal principal, ChallengeDbContext database, ScoreRecalculator recalculator, IHubContext<ChallengeHub> hub,
    ILogger<Program> logger, CancellationToken cancellationToken)
{
    await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
    var userId = GetUserId(principal);
    var participant = await GetParticipantAsync(database, challengeId, userId, cancellationToken);
    if (participant is null) return Results.Forbid();
    var challenge = await database.Challenges.SingleAsync(item => item.Id == challengeId, cancellationToken);
    if (challenge.Status is ChallengeStatus.Draft or ChallengeStatus.Completed or ChallengeStatus.Archived)
        return Results.Conflict(new { title = "Gameplay is unavailable while this challenge is a draft or read-only." });
    var careerId = await database.Careers.Where(item => item.ParticipantId == participant.Id)
        .Select(item => item.Id).SingleAsync(cancellationToken);
    NationalTeamStint? stint = null;
    if (stintId.HasValue)
    {
        stint = await database.NationalTeamStints.Include(item => item.SeasonMatches).SingleOrDefaultAsync(
            item => item.Id == stintId && item.CareerId == careerId, cancellationToken);
        if (stint is null) return Results.NotFound();
        if (version != stint.Version)
            return Results.Conflict(new { title = "This national team record changed. Refresh it before saving." });
    }
    else if (version.HasValue) return Results.Conflict(new { title = "A new national team record must not include a version." });
    var oldData = stint is null ? null : JsonSerializer.Serialize(stint);
    var eventType = request is null ? "nationalTeam.deleted" : stint is null ? "nationalTeam.created" : "nationalTeam.updated";
    if (request is not null)
    {
        if (string.IsNullOrWhiteSpace(request.TeamName) || request.TeamName.Length > 120
            || request.StartSeasonIndex is < 0 or > 100 || request.EndSeasonIndex < request.StartSeasonIndex
            || request.EndSeasonIndex > 100 || request.OfficialMatches is < 0 or > 5000 || request.Notes?.Length > 1000)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["nationalTeam"] = ["Enter a team name, valid season range and non-negative match count."] });
        var allocations = request.SeasonMatches ?? stint?.SeasonMatches
            .Select(item => new NationalTeamSeasonMatchRequest(item.SeasonIndex, item.OfficialMatches)).ToArray() ?? [];
        if (allocations.Any(item => item is null || item.SeasonIndex < request.StartSeasonIndex || item.SeasonIndex > (request.EndSeasonIndex ?? 100)
                || item.OfficialMatches is < 0 or > 5000)
            || allocations.Select(item => item.SeasonIndex).Distinct().Count() != allocations.Count
            || allocations.Sum(item => (long)item.OfficialMatches) > request.OfficialMatches)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["seasonMatches"] = ["Season allocations must be unique, within the stint and not exceed its match total."] });
        var countryCode = request.CountryCode is null ? stint?.CountryCode : request.CountryCode.Trim().ToUpperInvariant();
        if (!string.IsNullOrEmpty(countryCode) && !await database.Countries.AnyAsync(item => item.Code == countryCode, cancellationToken))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["countryCode"] = ["Choose a country from the catalog."] });
        var existing = await database.NationalTeamStints.Where(item => item.CareerId == careerId
            && item.StartSeasonIndex == request.StartSeasonIndex).ToListAsync(cancellationToken);
        if (existing.Any(item => item.Id != stintId && Same(item.TeamName, request.TeamName)))
            return Results.Conflict(new { title = "This national team stint has already been recorded." });
        if (stint is null)
        {
            stint = new NationalTeamStint { CareerId = careerId };
            database.NationalTeamStints.Add(stint);
        }
        stint.TeamName = request.TeamName.Trim();
        stint.CountryCode = string.IsNullOrEmpty(countryCode) ? null : countryCode;
        stint.StartSeasonIndex = request.StartSeasonIndex;
        stint.EndSeasonIndex = request.EndSeasonIndex;
        stint.OfficialMatches = request.OfficialMatches;
        stint.Notes = request.Notes?.Trim();
        if (request.SeasonMatches is not null)
        {
            foreach (var removed in stint.SeasonMatches.Where(item => !allocations.Any(row => row.SeasonIndex == item.SeasonIndex)).ToArray())
            {
                database.NationalTeamSeasonMatches.Remove(removed);
                stint.SeasonMatches.Remove(removed);
            }
            foreach (var row in allocations)
            {
                var existingRow = stint.SeasonMatches.SingleOrDefault(item => item.SeasonIndex == row.SeasonIndex);
                if (existingRow is null) stint.SeasonMatches.Add(new NationalTeamSeasonMatch
                    { NationalTeamStintId = stint.Id, SeasonIndex = row.SeasonIndex, OfficialMatches = row.OfficialMatches });
                else existingRow.OfficialMatches = row.OfficialMatches;
            }
        }
        stint.Version++;
    }
    else if (stint is not null) database.NationalTeamStints.Remove(stint);
    if (stint is null) return Results.NotFound();
    database.AuditLogs.Add(new AuditLog
    {
        ActorId = userId, ChallengeId = challengeId, EntityType = "NationalTeamStint",
        EntityId = stint.Id.ToString(), Action = eventType, OldData = oldData,
        NewData = request is null ? null : JsonSerializer.Serialize(stint),
    });
    database.ActivityEvents.Add(new ActivityEvent
    {
        ChallengeId = challengeId, ParticipantId = participant.Id, Type = eventType,
        Message = eventType == "nationalTeam.created" ? $"Recorded a national team stint with {stint.TeamName}."
            : eventType == "nationalTeam.updated" ? $"Updated a national team stint with {stint.TeamName}."
            : $"Removed a national team stint with {stint.TeamName}.",
        SeasonIndex = stint.StartSeasonIndex,
    });
    RaceScoringResult score;
    try
    {
        await database.SaveChangesAsync(cancellationToken);
        score = await recalculator.RecalculateAsync(challengeId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    catch (Exception exception) when (exception is DbUpdateConcurrencyException || IsSerializationFailure(exception))
    {
        logger.LogWarning(exception, "Concurrent national team update for challenge {ChallengeId}", challengeId);
        return Results.Conflict(new { title = "National team data changed. Refresh and try again." });
    }
    await hub.Clients.Group($"challenge:{challengeId}").SendAsync("career.clubChanged", new { challengeId }, cancellationToken);
    await hub.Clients.Group($"challenge:{challengeId}").SendAsync("score.updated", new { challengeId, scores = score.Participants }, cancellationToken);
    await hub.Clients.Group($"challenge:{challengeId}").SendAsync("challenge.updated", new { challengeId }, cancellationToken);
    await PublishUserUpdatesAsync(database, hub, challengeId, cancellationToken);
    if (request is null) return Results.NoContent();
    return stintId.HasValue ? Results.Ok(new { stint.Id, stint.Version })
        : Results.Created($"/api/challenges/{challengeId}/national-teams/{stint.Id}", new { stint.Id, stint.Version });
}

static async Task<IResult> ChangeTransferAsync(
    Guid challengeId, Guid? transferId, long? version, TransferRequest? request,
    ClaimsPrincipal principal, ChallengeDbContext database, ScoreRecalculator recalculator,
    IHubContext<ChallengeHub> hub, ILogger<Program> logger, CancellationToken cancellationToken)
{
    await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
    var userId = GetUserId(principal);
    var participant = await GetParticipantAsync(database, challengeId, userId, cancellationToken);
    if (participant is null) return Results.Forbid();
    var challenge = await database.Challenges.SingleAsync(item => item.Id == challengeId, cancellationToken);
    if (challenge.Status is ChallengeStatus.Draft or ChallengeStatus.Completed or ChallengeStatus.Archived)
        return Results.Conflict(new { title = "Gameplay is unavailable while this challenge is a draft or read-only." });
    var careerId = await database.Careers.Where(item => item.ParticipantId == participant.Id)
        .Select(item => item.Id).SingleAsync(cancellationToken);
    Transfer? transfer = null;
    if (transferId.HasValue)
    {
        transfer = await database.Transfers.SingleOrDefaultAsync(item => item.Id == transferId && item.CareerId == careerId, cancellationToken);
        if (transfer is null) return Results.NotFound();
        if (version != transfer.Version)
            return Results.Conflict(new { title = "This transfer changed. Refresh it before saving your changes." });
    }
    else if (version.HasValue) return Results.Conflict(new { title = "A new transfer must not include a version." });

    var oldData = transfer is null ? null : JsonSerializer.Serialize(transfer);
    var eventType = request is null ? "transfer.deleted" : transfer is null ? "transfer.created" : "transfer.updated";
    if (request is not null)
    {
        if (request.SeasonIndex is < 0 or > 100 || string.IsNullOrWhiteSpace(request.ClubName) || request.ClubName.Length > 120
            || string.IsNullOrWhiteSpace(request.PlayerName) || request.PlayerName.Length > 120
            || string.IsNullOrWhiteSpace(request.CounterpartyName) || request.CounterpartyName.Length > 120
            || request.Direction is not ("Incoming" or "Outgoing") || request.FixedAmount < 0
            || request.FixedAmount > 9999999999999.99m || decimal.Round(request.FixedAmount, 2) != request.FixedAmount
            || request.Currency is null || request.Currency.Trim().Length != 3 || !request.Currency.Trim().All(char.IsAsciiLetter)
            || request.Notes?.Length > 1000)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["transfer"] = ["Enter valid transfer details and a non-negative fixed amount with at most two decimal places."] });
        var season = await database.CareerSeasons.SingleOrDefaultAsync(
            item => item.CareerId == careerId && item.SeasonIndex == request.SeasonIndex, cancellationToken);
        if (season is null) return Results.Conflict(new { title = "Record the season before adding its transfers." });
        var currency = request.Currency.Trim().ToUpperInvariant();
        if (!string.Equals(season.Currency, currency, StringComparison.OrdinalIgnoreCase))
            return Results.Conflict(new { title = "The transfer currency must match the season currency." });
        var sameSeason = await database.Transfers.Where(item => item.CareerId == careerId && item.SeasonIndex == request.SeasonIndex)
            .ToListAsync(cancellationToken);
        if (sameSeason.Any(item => item.Id != transferId && item.Direction == request.Direction
            && Same(item.ClubName, request.ClubName) && Same(item.PlayerName, request.PlayerName)
            && Same(item.CounterpartyName, request.CounterpartyName)))
            return Results.Conflict(new { title = "This transfer is already recorded for the season." });
        if (transfer is null)
        {
            transfer = new Transfer { CareerId = careerId };
            database.Transfers.Add(transfer);
        }
        transfer.SeasonIndex = request.SeasonIndex;
        transfer.ClubName = request.ClubName.Trim();
        transfer.PlayerName = request.PlayerName.Trim();
        transfer.CounterpartyName = request.CounterpartyName.Trim();
        transfer.Direction = request.Direction;
        transfer.FixedAmount = request.FixedAmount;
        transfer.Currency = currency;
        transfer.Notes = request.Notes?.Trim();
        transfer.Version++;
    }
    else if (transfer is not null) database.Transfers.Remove(transfer);
    if (transfer is null) return Results.NotFound();

    database.AuditLogs.Add(new AuditLog
    {
        ActorId = userId, ChallengeId = challengeId, EntityType = "Transfer",
        EntityId = transfer.Id.ToString(), Action = eventType, OldData = oldData,
        NewData = request is null ? null : JsonSerializer.Serialize(transfer),
    });
    database.ActivityEvents.Add(new ActivityEvent
    {
        ChallengeId = challengeId, ParticipantId = participant.Id, Type = eventType,
        Message = eventType == "transfer.created" ? $"Recorded a transfer for {transfer.PlayerName}."
            : eventType == "transfer.updated" ? $"Corrected a transfer for {transfer.PlayerName}."
            : $"Removed a transfer for {transfer.PlayerName}.",
        ClubName = transfer.ClubName, SeasonIndex = transfer.SeasonIndex,
    });
    await QueueOtherParticipantNotificationsAsync(database, challengeId, participant.Id, eventType,
        "Transfer updated", $"Your rival updated a transfer for {transfer.PlayerName}.",
        transfer.Id.ToString(), cancellationToken);
    RaceScoringResult score;
    try
    {
        await database.SaveChangesAsync(cancellationToken);
        score = await recalculator.RecalculateAsync(challengeId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    catch (Exception exception) when (exception is DbUpdateConcurrencyException || IsSerializationFailure(exception))
    {
        logger.LogWarning(exception, "Concurrent transfer update for challenge {ChallengeId}", challengeId);
        return Results.Conflict(new { title = "Transfer data changed. Refresh and try again." });
    }
    await hub.Clients.Group($"challenge:{challengeId}").SendAsync(eventType, new { challengeId, transferId = transfer.Id }, cancellationToken);
    await hub.Clients.Group($"challenge:{challengeId}").SendAsync("score.updated", new { challengeId, scores = score.Participants }, cancellationToken);
    await hub.Clients.Group($"challenge:{challengeId}").SendAsync("challenge.updated", new { challengeId }, cancellationToken);
    await PublishUserUpdatesAsync(database, hub, challengeId, cancellationToken);
    if (request is null) return Results.NoContent();
    return transferId.HasValue ? Results.Ok(new { transfer.Id, transfer.Version })
        : Results.Created($"/api/challenges/{challengeId}/transfers/{transfer.Id}", new { transfer.Id, transfer.Version });
}

static async Task<IResult> ChangeCorrectionModeAsync(
    Guid challengeId, ChallengeCorrectionRequest request, bool reopen, ClaimsPrincipal principal,
    ChallengeDbContext database, ScoreRecalculator recalculator, IHubContext<ChallengeHub> hub,
    ILogger<Program> logger, CancellationToken cancellationToken)
{
    if (!request.Confirm) return Results.ValidationProblem(new Dictionary<string, string[]> { ["confirm"] = ["Confirmation is required."] });
    await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
    var userId = GetUserId(principal);
    var participant = await GetParticipantAsync(database, challengeId, userId, cancellationToken);
    if (participant is null || participant.Role != ParticipantRole.Administrator) return Results.Forbid();
    var challenge = await database.Challenges.SingleAsync(item => item.Id == challengeId, cancellationToken);
    if (reopen ? challenge.Status != ChallengeStatus.Completed
        : challenge.Status != ChallengeStatus.Active || !challenge.IsCorrectionMode)
        return Results.Conflict(new { title = reopen ? "Only a completed challenge can be reopened." : "This challenge is not in correction mode." });
    var oldData = JsonSerializer.Serialize(new
    {
        challenge.Status, challenge.IsCorrectionMode, challenge.WinningParticipantId,
        challenge.WinningSeasonIndex, challenge.CompletedAt, challenge.FinalScoreSnapshot, challenge.FinalRulesVersion,
    });
    challenge.IsCorrectionMode = reopen;
    if (reopen)
    {
        challenge.Status = ChallengeStatus.Active;
        challenge.WinningParticipantId = null;
        challenge.WinningSeasonIndex = null;
        challenge.CompletedAt = null;
        challenge.Version++;
    }
    var eventType = reopen ? "challenge.reopened" : "challenge.correctionsFinished";
    var audit = new AuditLog
    {
        ActorId = userId, ChallengeId = challengeId, EntityType = "Challenge",
        EntityId = challengeId.ToString(), Action = eventType, OldData = oldData,
    };
    database.AuditLogs.Add(audit);
    database.ActivityEvents.Add(new ActivityEvent
    {
        ChallengeId = challengeId, ParticipantId = participant.Id, Type = eventType,
        Message = reopen ? "Reopened the challenge for corrections." : "Finished challenge corrections.",
    });
    await QueueOtherParticipantNotificationsAsync(database, challengeId, participant.Id, eventType,
        reopen ? "Challenge reopened" : "Corrections finished",
        reopen ? "The challenge was reopened for corrections." : "The challenge corrections are complete.",
        challengeId.ToString(), cancellationToken);
    RaceScoringResult? score = null;
    try
    {
        if (!reopen) score = await recalculator.RecalculateAsync(challengeId, cancellationToken);
        audit.NewData = JsonSerializer.Serialize(new
        {
            challenge.Status, challenge.IsCorrectionMode, challenge.WinningParticipantId,
            challenge.WinningSeasonIndex, challenge.CompletedAt, challenge.FinalScoreSnapshot, challenge.FinalRulesVersion,
        });
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    catch (Exception exception) when (exception is DbUpdateConcurrencyException || IsSerializationFailure(exception))
    {
        logger.LogWarning(exception, "Concurrent correction mode change for challenge {ChallengeId}", challengeId);
        return Results.Conflict(new { title = "The challenge changed. Refresh and try again." });
    }
    await hub.Clients.Group($"challenge:{challengeId}").SendAsync("challenge.updated", new { challengeId }, cancellationToken);
    if (score is not null)
        await hub.Clients.Group($"challenge:{challengeId}").SendAsync("score.updated", new { challengeId, scores = score.Participants }, cancellationToken);
    await PublishUserUpdatesAsync(database, hub, challengeId, cancellationToken);
    return Results.Ok(new { Status = challenge.Status.ToString(), challenge.IsCorrectionMode, challenge.WinningParticipantId });
}

static async Task<Dictionary<string, string>> GetCountryContinentsAsync(ChallengeDbContext database, CancellationToken cancellationToken) =>
    (await database.Countries.AsNoTracking().ToListAsync(cancellationToken))
        .ToDictionary(item => item.Code, item => item.ContinentCode, StringComparer.OrdinalIgnoreCase);

static async Task<string?> ValidateClaimCompetitionAsync(
    ChallengeDbContext database, Guid challengeId, string code, Guid? competitionId, CancellationToken cancellationToken)
{
    if (competitionId is null) return null;
    var competition = await database.ChallengeCompetitions.SingleOrDefaultAsync(
        item => item.Id == competitionId && item.ChallengeId == challengeId, cancellationToken);
    if (competition is null) return "Choose a competition from this challenge.";
    var valid = code switch
    {
        "G020" or "G027" or "G028" or "G029" or "G030" => competition.Tier == "Top",
        "G021" or "G031" => competition.Tier == "Second",
        "G022" => competition.Tier == "Third",
        "G025" or "G026" => competition.Tier is "Top" or "Second" or "Third" or "Other",
        _ => false,
    };
    return valid ? null : "This competition is not compatible with the selected achievement.";
}

static async Task<IResult> ChangeCompetitionAsync(
    Guid challengeId, Guid? competitionId, CompetitionRequest request, ClaimsPrincipal principal,
    ChallengeDbContext database, IHubContext<ChallengeHub> hub, ILogger<Program> logger,
    CancellationToken cancellationToken, bool delete = false)
{
    await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
    var userId = GetUserId(principal);
    var participant = await GetParticipantAsync(database, challengeId, userId, cancellationToken);
    if (participant is null || participant.Role != ParticipantRole.Administrator) return Results.Forbid();
    var challenge = await database.Challenges.SingleAsync(item => item.Id == challengeId, cancellationToken);
    if (challenge.Status is ChallengeStatus.Completed or ChallengeStatus.Archived)
        return Results.Conflict(new { title = "This challenge is read-only." });
    if (!delete && (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 120
        || request.Tier is not ("Top" or "Second" or "Third" or "Other")))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["competition"] = ["Enter a competition name and a valid continental tier."] });
    ChallengeCompetition competition;
    string? oldData = null;
    if (competitionId.HasValue)
    {
        var stored = await database.ChallengeCompetitions.SingleOrDefaultAsync(
            item => item.Id == competitionId && item.ChallengeId == challengeId, cancellationToken);
        if (stored is null) return Results.NotFound();
        competition = stored;
        if (!request.Version.HasValue)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["version"] = ["A competition version is required."] });
        if (competition.Version != request.Version)
            return Results.Conflict(new { title = "This competition changed. Refresh it before saving." });
        oldData = JsonSerializer.Serialize(competition);
        if ((delete || request.Tier != competition.Tier)
            && await database.AchievementClaims.AnyAsync(item => item.CompetitionId == competition.Id, cancellationToken))
            return Results.Conflict(new { title = "A competition used by claims cannot be removed or change tier. Correct those claims first." });
    }
    else
    {
        competition = new ChallengeCompetition { ChallengeId = challengeId };
        database.ChallengeCompetitions.Add(competition);
    }
    if (delete) database.ChallengeCompetitions.Remove(competition);
    else
    {
        competition.Name = request.Name!.Trim();
        competition.NormalizedName = competition.Name.ToUpperInvariant();
        competition.Tier = request.Tier!;
        if (competitionId.HasValue) competition.Version++;
    }
    challenge.Version++;
    database.AuditLogs.Add(new AuditLog
    {
        ActorId = userId, ChallengeId = challengeId, EntityType = "ChallengeCompetition",
        EntityId = competition.Id.ToString(), Action = delete ? "Deleted" : competitionId.HasValue ? "Updated" : "Created",
        OldData = oldData, NewData = delete ? null : JsonSerializer.Serialize(competition),
    });
    database.ActivityEvents.Add(new ActivityEvent
    {
        ChallengeId = challengeId, ParticipantId = participant.Id, Type = "challenge.rulesUpdated",
        Message = delete ? $"Removed competition {competition.Name}." : $"Updated competition {competition.Name}.",
    });
    await QueueOtherParticipantNotificationsAsync(database, challengeId, participant.Id, "challenge.rulesUpdated",
        "Competition catalog updated", $"The competition catalog changed for {competition.Name}.", competition.Id.ToString(), cancellationToken);
    try
    {
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    catch (Exception exception) when (exception is DbUpdateConcurrencyException || IsSerializationFailure(exception)
        || IsUniqueConstraintViolation(exception, "IX_ChallengeCompetitions_ChallengeId_NormalizedName"))
    {
        logger.LogWarning(exception, "Concurrent or duplicate competition change for challenge {ChallengeId}", challengeId);
        return Results.Conflict(new { title = "The competition catalog changed or that name already exists. Refresh and try again." });
    }
    await hub.Clients.Group($"challenge:{challengeId}").SendAsync("challenge.rulesUpdated", new { challengeId }, cancellationToken);
    await PublishUserUpdatesAsync(database, hub, challengeId, cancellationToken);
    return delete ? Results.NoContent() : Results.Ok(new { competition.Id, competition.Name, competition.Tier, competition.Version });
}

static bool IsValidCareerStint(CareerStintCreateRequest request) =>
    !string.IsNullOrWhiteSpace(request.ClubName) && request.ClubName.Trim().Length <= 120
    && !string.IsNullOrWhiteSpace(request.CountryCode) && request.CountryCode.Trim().Length <= 8
    && !string.IsNullOrWhiteSpace(request.CountryName) && request.CountryName.Trim().Length <= 80
    && request.StartSeasonIndex is >= 0 and <= 100
    && (request.EndSeasonIndex is null || request.EndSeasonIndex >= request.StartSeasonIndex && request.EndSeasonIndex <= 100)
    && request.InitialDivisionLevel is >= 1 and <= 20
    && request.OfficialMatches is >= 0 and <= 5000
    && (request.EligibleSeasonIndex is null
        || request.EligibleSeasonIndex >= request.StartSeasonIndex && request.EligibleSeasonIndex <= 100
            && (!request.EndSeasonIndex.HasValue || request.EligibleSeasonIndex <= request.EndSeasonIndex));

static async Task<bool> IsValidClubMilestoneAsync(
    ChallengeDbContext database, Guid participantId, CareerStint candidate, CancellationToken cancellationToken)
{
    if (!candidate.EligibleSeasonIndex.HasValue) return true;
    var records = await database.CareerStints.AsNoTracking()
        .Where(item => item.CareerId == candidate.CareerId && item.Id != candidate.Id).ToListAsync(cancellationToken);
    records.Add(candidate);
    var inputs = await ClubInputLoader.LoadAsync(database, records,
        new Dictionary<Guid, Guid> { [candidate.CareerId] = participantId }, cancellationToken);
    return RaceScoringEngine.QualifiesClubMilestone(inputs[^1], inputs);
}

static Task<string?> ValidateStintCatalogClubAsync(ChallengeDbContext database, CareerStintCreateRequest request, CancellationToken cancellationToken) =>
    ValidateCatalogClubAsync(database, request.ClubId, request.ClearClubId, request.ClubName, request.CountryCode, cancellationToken);

static async Task<string?> ValidateCareerStintIdentityAsync(
    ChallengeDbContext database, Guid careerId, CareerStintCreateRequest request, CareerStint? existing,
    CancellationToken cancellationToken)
{
    var name = request.ClubName.Trim().ToUpperInvariant();
    var country = request.CountryCode.Trim().ToUpperInvariant();
    var effectiveClubId = request.ClearClubId ? null : request.ClubId
        ?? (existing is not null && Same(existing.ClubName, request.ClubName) && Same(existing.CountryCode, request.CountryCode)
            ? existing.ClubId : null);
    var identities = await database.Clubs.CountAsync(item =>
        item.CountryCode == country && item.NormalizedName == name, cancellationToken);
    if (identities < 2) return null;
    if (!effectiveClubId.HasValue)
        return "Multiple catalog clubs share this name and country. Select the intended club identity before saving this career stint.";

    var identityChanged = existing is null || existing.ClubId != effectiveClubId
        || !Same(existing.ClubName, request.ClubName) || !Same(existing.CountryCode, request.CountryCode);
    var explicitlyResolvingThisStint = existing is { ClubId: null } && request.ClubId.HasValue;
    if (!identityChanged || explicitlyResolvingThisStint) return null;

    var existingStintId = existing?.Id;
    var unresolvedMatches = await database.CareerStints.AnyAsync(item =>
        item.CareerId == careerId && (!existingStintId.HasValue || item.Id != existingStintId.Value) && item.ClubId == null
        && item.ClubName.Trim().ToUpper() == name && item.CountryCode.Trim().ToUpper() == country,
        cancellationToken);
    return unresolvedMatches
        ? "Link existing text-only stints to their intended catalog clubs before adding or changing another same-name club in this career."
        : null;
}

static async Task<string?> ValidateCatalogClubAsync(ChallengeDbContext database, Guid? clubId, bool clearClubId,
    string? clubName, string? countryCode, CancellationToken cancellationToken)
{
    if (clearClubId && clubId.HasValue)
        return "Choose a catalog club or clear the link, not both.";
    if (!clubId.HasValue) return null;
    var club = await database.Clubs.AsNoTracking().SingleOrDefaultAsync(item => item.Id == clubId, cancellationToken);
    if (club is null) return "Choose an existing club from the catalog.";
    if (!Same(club.Name, clubName) || !Same(club.CountryCode, countryCode))
        return "The club name and country code must match the selected catalog club.";
    return null;
}

static async Task RefreshCurrentClubAsync(ChallengeDbContext database, Career career, CancellationToken cancellationToken)
{
    var stints = await database.CareerStints.Where(item => item.CareerId == career.Id && item.EndSeasonIndex == null)
        .OrderByDescending(item => item.StartSeasonIndex).ThenBy(item => item.ClubName).ThenBy(item => item.Id)
        .ToListAsync(cancellationToken);
    var latest = stints.FirstOrDefault()?.StartSeasonIndex;
    var current = stints.FirstOrDefault(item => item.StartSeasonIndex == latest
        && Same(item.ClubName, career.CurrentClubName) && Same(item.CountryCode, career.CurrentCountryCode))
        ?? stints.FirstOrDefault();
    career.CurrentClubName = current?.ClubName;
    career.CurrentCountryCode = current?.CountryCode;
}

static async Task<IResult> ChangeCareerStintAsync(
    Guid challengeId, Guid stintId, long version, CareerStintCreateRequest? request,
    ClaimsPrincipal principal, ChallengeDbContext database, ScoreRecalculator recalculator,
    IHubContext<ChallengeHub> hub, ILogger<Program> logger, CancellationToken cancellationToken)
{
    await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
    var userId = GetUserId(principal);
    var participant = await GetParticipantAsync(database, challengeId, userId, cancellationToken);
    if (participant is null) return Results.Forbid();
    var challenge = await database.Challenges.SingleAsync(item => item.Id == challengeId, cancellationToken);
    if (challenge.Status is ChallengeStatus.Draft or ChallengeStatus.Completed or ChallengeStatus.Archived)
        return Results.Conflict(new { title = "Gameplay is unavailable while this challenge is a draft or read-only." });
    var career = await database.Careers.SingleAsync(item => item.ParticipantId == participant.Id, cancellationToken);
    var stint = await database.CareerStints.SingleOrDefaultAsync(item => item.Id == stintId && item.CareerId == career.Id, cancellationToken);
    if (stint is null) return Results.NotFound();
    if (stint.Version != version)
        return Results.Conflict(new { title = "This club stint changed. Refresh it before saving your changes." });
    if (request is not null && !IsValidCareerStint(request))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["career"] = ["Enter valid club details and a milestone season within the stint, if provided."] });
    if (career.StartingStintId.HasValue)
    {
        if (career.StartingStintId == stint.Id)
        {
            if (request is null)
                return Results.Conflict(new { title = "Change the declared career start before removing this club." });
            if (request.InitialDivisionLevel <= 1 || await database.CareerStints.AnyAsync(item =>
                    item.CareerId == career.Id && item.Id != stint.Id && item.StartSeasonIndex < request.StartSeasonIndex, cancellationToken))
                return Results.Conflict(new { title = "The declared starting club must remain below the first division and in the earliest recorded season." });
            if ((!Same(stint.ClubName, request.ClubName) || !Same(stint.CountryCode, request.CountryCode))
                && await database.AchievementClaims.AnyAsync(item => item.ParticipantId == participant.Id
                    && (item.AchievementCode == "G037" || item.AchievementCode == "G041")
                    && item.StartingCareerClub != null, cancellationToken))
                return Results.Conflict(new { title = "Remove existing club and country move claims before changing the declared starting club's identity." });
        }
        else if (request is not null)
        {
            var startingSeason = await database.CareerStints.Where(item => item.Id == career.StartingStintId)
                .Select(item => item.StartSeasonIndex).SingleAsync(cancellationToken);
            if (request.StartSeasonIndex < startingSeason)
                return Results.Conflict(new { title = "This stint starts before the declared starting club. Correct your career start first." });
        }
    }
    if (request is not null)
    {
        var clubError = await ValidateStintCatalogClubAsync(database, request, cancellationToken);
        if (clubError is not null)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["clubId"] = [clubError] });
        var identityError = await ValidateCareerStintIdentityAsync(database, career.Id, request, stint, cancellationToken);
        if (identityError is not null)
            return Results.Conflict(new { title = identityError });
    }

    var recordedSeasons = await (
        from entry in database.ClubSeasonStatistics
        join season in database.CareerSeasons on entry.CareerSeasonId equals season.Id
        where entry.CareerStintId == stintId
        select new { season.SeasonIndex, entry.OfficialMatches }).ToListAsync(cancellationToken);
    if (request is null && recordedSeasons.Count > 0)
        return Results.Conflict(new { title = "Remove this stint from its season breakdowns before deleting it." });
    if (request is not null && (recordedSeasons.Sum(item => (long)item.OfficialMatches) > request.OfficialMatches
        || recordedSeasons.Any(item => item.SeasonIndex < request.StartSeasonIndex || item.SeasonIndex > (request.EndSeasonIndex ?? 100))))
        return Results.Conflict(new { title = "The stint's season breakdowns are outside the requested dates or match total. Correct them first." });

    var oldData = JsonSerializer.Serialize(stint);
    var wasCurrent = Same(stint.ClubName, career.CurrentClubName) && Same(stint.CountryCode, career.CurrentCountryCode);
    var action = request is null ? "Deleted" : "Updated";
    if (request is null) database.CareerStints.Remove(stint);
    else
    {
        stint.ClubId = request.ClearClubId ? null : request.ClubId
            ?? (Same(stint.ClubName, request.ClubName) && Same(stint.CountryCode, request.CountryCode) ? stint.ClubId : null);
        stint.ClubName = request.ClubName.Trim();
        stint.CountryName = request.CountryName.Trim();
        stint.CountryCode = request.CountryCode.Trim().ToUpperInvariant();
        stint.StartSeasonIndex = request.StartSeasonIndex;
        stint.EndSeasonIndex = request.EndSeasonIndex;
        stint.InitialDivisionLevel = request.InitialDivisionLevel;
        stint.OfficialMatches = request.OfficialMatches;
        stint.EligibleSeasonIndex = request.EligibleSeasonIndex;
        if (!await IsValidClubMilestoneAsync(database, participant.Id, stint, cancellationToken))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["eligibleSeasonIndex"] = ["Record at least 20 matches across this club's stints started by the milestone season, including a match in the milestone stint."] });
        stint.Version++;
        if (wasCurrent && stint.EndSeasonIndex is null)
        {
            career.CurrentClubName = stint.ClubName;
            career.CurrentCountryCode = stint.CountryCode;
        }
    }
    database.AuditLogs.Add(new AuditLog
    {
        ActorId = userId, ChallengeId = challengeId, EntityType = "CareerStint", EntityId = stint.Id.ToString(),
        Action = action, OldData = oldData, NewData = request is null ? null : JsonSerializer.Serialize(stint),
    });
    database.ActivityEvents.Add(new ActivityEvent
    {
        ChallengeId = challengeId, ParticipantId = participant.Id, Type = "career.clubChanged",
        Message = request is null ? $"Removed {stint.ClubName} from the career." : $"Corrected the {stint.ClubName} career stint.",
        ClubName = stint.ClubName, SeasonIndex = stint.StartSeasonIndex,
    });
    RaceScoringResult score;
    try
    {
        await database.SaveChangesAsync(cancellationToken);
        await RefreshCurrentClubAsync(database, career, cancellationToken);
        score = await recalculator.RecalculateAsync(challengeId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    catch (Exception exception) when (exception is DbUpdateConcurrencyException || IsSerializationFailure(exception))
    {
        logger.LogWarning(exception, "Concurrent club stint correction for challenge {ChallengeId}", challengeId);
        return Results.Conflict(new { title = "This club stint changed. Refresh it before saving your changes." });
    }
    await hub.Clients.Group($"challenge:{challengeId}").SendAsync("career.clubChanged", new { challengeId, stintId }, cancellationToken);
    await hub.Clients.Group($"challenge:{challengeId}").SendAsync("score.updated", new { challengeId, scores = score.Participants }, cancellationToken);
    await hub.Clients.Group($"challenge:{challengeId}").SendAsync("challenge.updated", new { challengeId }, cancellationToken);
    await PublishUserUpdatesAsync(database, hub, challengeId, cancellationToken);
    return request is null ? Results.NoContent() : Results.Ok(new { stint.Id, stint.Version });
}

static async Task<IResult> ChangeAchievementClaimAsync(
    Guid challengeId, Guid claimId, long version, ClaimUpdateRequest? request,
    ClaimsPrincipal principal, ChallengeDbContext database, ScoreRecalculator recalculator,
    IHubContext<ChallengeHub> hub, ILogger<Program> logger, CancellationToken cancellationToken)
{
    await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
    var userId = GetUserId(principal);
    var participant = await GetParticipantAsync(database, challengeId, userId, cancellationToken);
    if (participant is null) return Results.Forbid();
    var challenge = await database.Challenges.SingleAsync(item => item.Id == challengeId, cancellationToken);
    if (challenge.Status is ChallengeStatus.Draft or ChallengeStatus.Completed or ChallengeStatus.Archived)
        return Results.Conflict(new { title = "Gameplay is unavailable while this challenge is a draft or read-only." });
    var claim = await database.AchievementClaims.SingleOrDefaultAsync(
        item => item.Id == claimId && item.ParticipantId == participant.Id, cancellationToken);
    if (claim is null) return Results.NotFound();
    if (claim.Version != version)
        return Results.Conflict(new { title = "This achievement changed. Refresh it before saving your changes." });

    var definition = await database.AchievementDefinitions.SingleAsync(
        item => item.Code == claim.AchievementCode && (item.ChallengeId == null || item.ChallengeId == challengeId), cancellationToken);
    var oldData = JsonSerializer.Serialize(claim);
    var eventType = request is null ? "achievement.deleted" : "achievement.updated";
    if (request is not null)
    {
        if (claim.AchievementCode == "G053")
            return Results.Conflict(new { title = "Set the three continents milestone in your career instead." });
        var claimRule = await database.ChallengeRules.Where(item => item.ChallengeId == challengeId
            && item.AchievementCode == claim.AchievementCode && item.EffectiveSeasonIndex <= request.SeasonIndex)
            .OrderByDescending(item => item.VersionNumber).FirstOrDefaultAsync(cancellationToken);
        var frequency = claimRule?.FrequencyOverride ?? definition.Frequency;
        if (request.SeasonIndex is < 0 or > 100 || request.ClubName?.Length > 120
            || request.CountryCode?.Length > 8 || request.Notes?.Length > 1000
            || (frequency is AchievementFrequency.PerClub or AchievementFrequency.ClubSeason && string.IsNullOrWhiteSpace(request.ClubName))
            || (frequency == AchievementFrequency.PerCountry && string.IsNullOrWhiteSpace(request.CountryCode)))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["claim"] = ["Enter a valid season and the required achievement details."] });
        var participationErrors = ValidateCompetitionParticipation(claim.AchievementCode, request, claimRule?.MinimumCupMatchesOverride ?? 2);
        if (participationErrors.Count > 0) return Results.ValidationProblem(participationErrors);
        var competitionError = await ValidateClaimCompetitionAsync(database, challengeId, claim.AchievementCode, request.CompetitionId, cancellationToken);
        if (competitionError is not null)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["competitionId"] = [competitionError] });
        var clubError = await ValidateCatalogClubAsync(database, request.ClubId, request.ClearClubId,
            request.ClubName, request.CountryCode, cancellationToken);
        if (clubError is not null)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["clubId"] = [clubError] });
        if (claim.AchievementCode == "G013")
        {
            var doubleError = await ValidateDomesticDoubleConsistencyAsync(database, participant.Id, claim.Id, request, cancellationToken);
            if (doubleError is not null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["domesticDouble"] = [doubleError] });
        }
        if (claim.AchievementCode == "G032")
        {
            var firstClubError = await ValidateCatalogClubAsync(database, request.FirstContinentalTitleClubId, request.ClearFirstContinentalTitleClubId,
                request.FirstContinentalTitleClub, request.FirstContinentalTitleCountry, cancellationToken);
            if (firstClubError is not null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["firstContinentalTitleClubId"] = [firstClubError] });
        }
        if (claim.AchievementCode is "G037" or "G041")
        {
            var startingError = await ValidateCatalogClubAsync(database, request.StartingCareerClubId, request.ClearStartingCareerClubId,
                request.StartingCareerClub, request.StartingCareerCountry, cancellationToken);
            if (startingError is not null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["startingCareerClubId"] = [startingError] });
            var moveError = await ValidateCareerMoveAsync(database, participant.Id, claim.CareerId, claim.Id, claim.AchievementCode, request, cancellationToken);
            if (moveError is not null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["careerMove"] = [moveError] });
        }
        if (claim.AchievementCode == "G038")
        {
            var divisionError = await ValidateFirstDivisionExperienceAsync(database, participant.Id, claim.CareerId, request.SeasonIndex, cancellationToken);
            if (divisionError is not null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["firstDivisionExperience"] = [divisionError] });
        }
        if (claim.AchievementCode == "G039")
        {
            var divisionError = await ValidateHigherDivisionMoveAsync(database, participant.Id, claim.CareerId, request.SeasonIndex,
                request.PreviousBestDivisionLevel, cancellationToken);
            if (divisionError is not null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["previousBestDivisionLevel"] = [divisionError] });
        }
        if (claim.AchievementCode == "G050")
        {
            var continuityError = await ValidateThirdToFirstContinuityAsync(database, participant.Id, claim.CareerId, request, cancellationToken);
            if (continuityError is not null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["stayedWithClubFromThirdToFirst"] = [continuityError] });
        }
        if (claim.AchievementCode == "G040")
        {
            var trophyError = await ValidateFirstMajorClubTrophyAsync(database, participant.Id, claim.Id, request, cancellationToken);
            if (trophyError is not null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["firstMajorClubTrophy"] = [trophyError] });
        }
        var occurrence = $"{GetOccurrenceKey(frequency, request.SeasonIndex, request.ClubName, request.CountryCode)}:claim:{claim.Id:N}";
        var existingClaims = await database.AchievementClaims.Where(item => item.Id != claimId && item.ParticipantId == participant.Id
            && item.AchievementCode == claim.AchievementCode).ToListAsync(cancellationToken);
        var candidate = new ClaimInput(claim.Id, participant.Id, claim.AchievementCode, request.SeasonIndex,
            request.ClubName, request.CountryCode, claim.CreatedAt, ClubId: request.ClearClubId ? null : request.ClubId
                ?? (Same(claim.ClubName, request.ClubName) && Same(claim.CountryCode, request.CountryCode) ? claim.ClubId : null));
        var duplicate = await HasFrequencyConflictAsync(database, challengeId, definition, existingClaims, candidate, cancellationToken);
        if (duplicate)
            return Results.Conflict(new { title = FrequencyConflictMessage(frequency) });
        if (claim.AchievementCode == "G037" && await database.AchievementClaims.AnyAsync(item =>
            item.Id != claimId && item.ParticipantId == participant.Id && item.AchievementCode == "G037"
            && item.SeasonIndex == request.SeasonIndex, cancellationToken))
            return Results.Conflict(new { title = "Only one club move can score in a season." });
        if (await HasConflictingContinentalParticipationAsync(database, participant.Id, claim.Id, claim.AchievementCode,
                request.ClubName, request.CountryCode, request.SeasonIndex, cancellationToken))
            return Results.Conflict(new { title = "The first and later continental participations for this club have conflicting seasons." });
        claim.SeasonIndex = request.SeasonIndex;
        claim.ClubId = request.ClearClubId ? null : request.ClubId
            ?? (Same(claim.ClubName, request.ClubName) && Same(claim.CountryCode, request.CountryCode) ? claim.ClubId : null);
        claim.ClubName = request.ClubName?.Trim();
        claim.CountryCode = request.CountryCode?.Trim().ToUpperInvariant();
        claim.Notes = request.Notes?.Trim();
        SetCompetitionParticipation(claim, request);
        claim.OccurrenceKey = occurrence;
        claim.UpdatedAt = DateTimeOffset.UtcNow;
        claim.Version++;
    }
    else database.AchievementClaims.Remove(claim);

    var achievementName = await database.ChallengeRules
        .Where(item => item.ChallengeId == challengeId && item.AchievementCode == claim.AchievementCode
            && item.EffectiveSeasonIndex <= claim.SeasonIndex)
        .OrderByDescending(item => item.VersionNumber).Select(item => item.NameOverride)
        .FirstOrDefaultAsync(cancellationToken) ?? definition.Name;
    database.AuditLogs.Add(new AuditLog
    {
        ActorId = userId, ChallengeId = challengeId, EntityType = "AchievementClaim",
        EntityId = claim.Id.ToString(), Action = eventType, OldData = oldData,
        NewData = request is null ? null : JsonSerializer.Serialize(claim),
    });
    database.ActivityEvents.Add(new ActivityEvent
    {
        ChallengeId = challengeId, ParticipantId = participant.Id, Type = eventType,
        Message = request is null ? $"Removed {achievementName}." : $"Corrected {achievementName}.",
        ClubName = claim.ClubName, SeasonIndex = claim.SeasonIndex,
    });
    await QueueOtherParticipantNotificationsAsync(database, challengeId, participant.Id, eventType,
        request is null ? "Achievement removed" : "Achievement corrected",
        request is null ? $"Your rival removed {achievementName}." : $"Your rival corrected {achievementName}.",
        claim.Id.ToString(), cancellationToken);
    RaceScoringResult score;
    try
    {
        await database.SaveChangesAsync(cancellationToken);
        score = await recalculator.RecalculateAsync(challengeId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    catch (Exception exception) when (exception is DbUpdateConcurrencyException || IsSerializationFailure(exception)
        || IsUniqueConstraintViolation(exception, "IX_AchievementClaims_ParticipantId_AchievementCode_OccurrenceK~"))
    {
        logger.LogWarning(exception, "Concurrent achievement correction for challenge {ChallengeId}", challengeId);
        return Results.Conflict(new { title = "This achievement changed. Refresh it before saving your changes." });
    }
    await hub.Clients.Group($"challenge:{challengeId}").SendAsync(eventType, new { challengeId, claimId }, cancellationToken);
    await hub.Clients.Group($"challenge:{challengeId}").SendAsync("score.updated", new { challengeId, scores = score.Participants }, cancellationToken);
    await hub.Clients.Group($"challenge:{challengeId}").SendAsync("challenge.updated", new { challengeId }, cancellationToken);
    await PublishUserUpdatesAsync(database, hub, challengeId, cancellationToken);
    return request is null ? Results.NoContent() : Results.Ok(new { claim.Id, claim.Version });
}

static void SetCompetitionParticipation(AchievementClaim claim, ICompetitionEvidence evidence)
{
    var code = claim.AchievementCode;
    var league = code is "G001" or "G002" or "G003" or "G004" or "G011" or "G013" or "G014" or "G019";
    var cup = code is "G008" or "G009" or "G015";
    claim.ManagedCompetitionMatches = league || cup ? evidence.ManagedCompetitionMatches : null;
    claim.TotalCompetitionMatches = league ? evidence.TotalCompetitionMatches : null;
    claim.InChargeAtCompetitionEnd = league && code != "G011" ? evidence.InChargeAtCompetitionEnd : null;
    claim.ManagedDecidingMatch = cup || code is "G010" or "G013" or "G023" ? evidence.ManagedDecidingMatch : null;
    claim.FirstSeasonAfterPromotion = code is "G011" or "G016" or "G019" ? evidence.FirstSeasonAfterPromotion : null;
    claim.AvoidedRelegation = code == "G011" ? evidence.AvoidedRelegation : null;
    claim.CompetitionLeaguePosition = code is "G012" or "G016" or "G019" ? evidence.CompetitionLeaguePosition : null;
    claim.WonFirstDivisionTitle = code == "G013" ? evidence.WonFirstDivisionTitle : null;
    claim.WonMajorNationalCup = code == "G013" ? evidence.WonMajorNationalCup : null;
    claim.WonLeagueTitleUnbeaten = code == "G014" ? evidence.WonLeagueTitleUnbeaten : null;
    claim.CupWonInLowerDivision = code == "G015" ? evidence.CupWonInLowerDivision : null;
    claim.DomesticCupMatchesManaged = code == "G013" ? evidence.DomesticCupMatchesManaged : null;
    claim.PromotionDestinationLevel = code is "G005" or "G006" or "G007" ? evidence.PromotionDestinationLevel : null;
    claim.PromotionWithoutTitle = code is "G005" or "G006" or "G007" ? evidence.PromotionWithoutTitle : null;
    claim.PromotionStreakStartSeason = code is "G017" or "G018" ? evidence.PromotionStreakStartSeason : null;
    claim.ReachedContinentalMainPhase = code is "G025" or "G026" or "G027" ? evidence.ReachedContinentalMainPhase : null;
    claim.PriorContinentalMainPhase = code is "G025" or "G026" ? evidence.PriorContinentalMainPhase : null;
    claim.FirstTopTierMainPhase = code == "G027" ? evidence.FirstTopTierMainPhase : null;
    claim.FirstContinentalTitleClubId = code != "G032" || evidence.ClearFirstContinentalTitleClubId ? null : evidence.FirstContinentalTitleClubId
        ?? (Same(claim.FirstContinentalTitleClub, evidence.FirstContinentalTitleClub)
            && Same(claim.FirstContinentalTitleCountry, evidence.FirstContinentalTitleCountry) ? claim.FirstContinentalTitleClubId : null);
    claim.FirstContinentalTitleClub = code == "G032" ? evidence.FirstContinentalTitleClub?.Trim() : null;
    claim.FirstContinentalTitleCountry = code == "G032" ? evidence.FirstContinentalTitleCountry?.Trim().ToUpperInvariant() : null;
    claim.FirstContinentalTitleSeason = code == "G032" ? evidence.FirstContinentalTitleSeason : null;
    claim.WonContinentalTitlesWithBothClubs = code == "G032" ? evidence.WonContinentalTitlesWithBothClubs : null;
    var careerMove = code is "G037" or "G041";
    claim.StartingCareerClubId = !careerMove || evidence.ClearStartingCareerClubId ? null : evidence.StartingCareerClubId
        ?? (Same(claim.StartingCareerClub, evidence.StartingCareerClub) && Same(claim.StartingCareerCountry, evidence.StartingCareerCountry)
            ? claim.StartingCareerClubId : null);
    claim.StartingCareerClub = careerMove ? evidence.StartingCareerClub?.Trim() : null;
    claim.StartingCareerCountry = careerMove ? evidence.StartingCareerCountry?.Trim().ToUpperInvariant() : null;
    claim.FirstDivisionExperience = code == "G038" ? evidence.FirstDivisionExperience : null;
    claim.HigherDivisionClubMove = code == "G039" ? evidence.HigherDivisionClubMove : null;
    claim.PreviousBestDivisionLevel = code == "G039" ? evidence.PreviousBestDivisionLevel : null;
    claim.NewDivisionLevel = code == "G039" ? evidence.NewDivisionLevel : null;
    claim.FirstMajorClubTrophy = code == "G040" ? evidence.FirstMajorClubTrophy : null;
    claim.TrophyNotSuperCup = code == "G040" ? evidence.TrophyNotSuperCup : null;
    claim.WonTenMajorTrophies = code == "G048" ? evidence.WonTenMajorTrophies : null;
    claim.ThirdDivisionStartSeason = code == "G050" ? evidence.ThirdDivisionStartSeason : null;
    claim.StayedWithClubFromThirdToFirst = code == "G050" ? evidence.StayedWithClubFromThirdToFirst : null;
    claim.OfficialManagerOfYear = code == "G051" ? evidence.OfficialManagerOfYear : null;
    var hasCompetitionResult = CompetitionResultRequirement(code) is not null;
    claim.CompetitionResultKind = hasCompetitionResult ? evidence.CompetitionResultKind : null;
    claim.CompetitionResultStage = hasCompetitionResult ? evidence.CompetitionResultStage : null;
    claim.CompetitionId = evidence.CompetitionId;
}

static async Task<string?> ValidateCareerMoveAsync(
    ChallengeDbContext database, Guid participantId, Guid careerId, Guid? excludingClaimId,
    string code, ICompetitionEvidence evidence, CancellationToken cancellationToken)
{
    if (evidence.StartingCareerClub?.Trim().Length > 120 || evidence.StartingCareerCountry?.Trim().Length > 8)
        return "The starting club or country code is too long.";
    var earlierChoices = await database.AchievementClaims.Where(item => item.ParticipantId == participantId
            && item.Id != excludingClaimId && (item.AchievementCode == "G037" || item.AchievementCode == "G041")
            && item.StartingCareerClub != null)
        .Select(item => new { item.StartingCareerClub, item.StartingCareerCountry }).ToListAsync(cancellationToken);
    if (earlierChoices.Any(item => !Same(item.StartingCareerClub, evidence.StartingCareerClub)
        || !Same(item.StartingCareerCountry, evidence.StartingCareerCountry)))
        return "Use the same starting club and country as your other club-move claims.";
    var stints = await database.CareerStints.Where(item => item.CareerId == careerId).ToListAsync(cancellationToken);
    var startingStintId = await database.Careers.Where(item => item.Id == careerId)
        .Select(item => item.StartingStintId).SingleAsync(cancellationToken);
    var declaredStart = stints.SingleOrDefault(item => item.Id == startingStintId);
    if (declaredStart is not null && (!Same(declaredStart.ClubName, evidence.StartingCareerClub)
        || !Same(declaredStart.CountryCode, evidence.StartingCareerCountry)))
        return "Use the declared starting club and country from your career.";
    var clubs = await ClubInputLoader.LoadAsync(database, stints,
        new Dictionary<Guid, Guid> { [careerId] = participantId }, cancellationToken);
    return RaceScoringEngine.ValidateCareerMove(new ClaimInput(Guid.Empty, participantId, code, evidence.SeasonIndex,
        evidence.ClubName, evidence.CountryCode, DateTimeOffset.UtcNow, evidence.StartingCareerClub, evidence.StartingCareerCountry,
        ClubId: evidence.ClubId, StartingCareerClubId: evidence.StartingCareerClubId), clubs);
}

static async Task<string?> ValidateFirstDivisionExperienceAsync(
    ChallengeDbContext database, Guid participantId, Guid careerId, int seasonIndex, CancellationToken cancellationToken)
{
    var stints = await database.CareerStints.Where(item => item.CareerId == careerId).ToListAsync(cancellationToken);
    var clubs = await ClubInputLoader.LoadAsync(database, stints,
        new Dictionary<Guid, Guid> { [careerId] = participantId }, cancellationToken);
    return RaceScoringEngine.ValidateFirstDivisionExperience(
        new ClaimInput(Guid.Empty, participantId, "G038", seasonIndex, null, null, DateTimeOffset.UtcNow), clubs);
}

static async Task<string?> ValidateHigherDivisionMoveAsync(
    ChallengeDbContext database, Guid participantId, Guid careerId, int seasonIndex, int? previousBestDivisionLevel,
    CancellationToken cancellationToken)
{
    var stints = await database.CareerStints.Where(item => item.CareerId == careerId).ToListAsync(cancellationToken);
    var clubs = await ClubInputLoader.LoadAsync(database, stints,
        new Dictionary<Guid, Guid> { [careerId] = participantId }, cancellationToken);
    return RaceScoringEngine.ValidateHigherDivisionMove(
        new ClaimInput(Guid.Empty, participantId, "G039", seasonIndex, null, null, DateTimeOffset.UtcNow,
            PreviousBestDivisionLevel: previousBestDivisionLevel), clubs);
}

static async Task<string?> ValidateThirdToFirstContinuityAsync(
    ChallengeDbContext database, Guid participantId, Guid careerId, ICompetitionEvidence evidence, CancellationToken cancellationToken)
{
    var stints = await database.CareerStints.Where(item => item.CareerId == careerId).ToListAsync(cancellationToken);
    var clubs = await ClubInputLoader.LoadAsync(database, stints,
        new Dictionary<Guid, Guid> { [careerId] = participantId }, cancellationToken);
    return RaceScoringEngine.ValidateThirdToFirstContinuity(
        new ClaimInput(Guid.Empty, participantId, "G050", evidence.SeasonIndex, evidence.ClubName, evidence.CountryCode,
            DateTimeOffset.UtcNow, ClubId: evidence.ClubId, ThirdDivisionStartSeason: evidence.ThirdDivisionStartSeason), clubs);
}

static async Task<string?> ValidateFirstMajorClubTrophyAsync(
    ChallengeDbContext database, Guid participantId, Guid? claimId, ICompetitionEvidence evidence, CancellationToken cancellationToken)
{
    var claims = await database.AchievementClaims.Where(item => item.ParticipantId == participantId
        && item.Id != claimId && item.SeasonIndex < evidence.SeasonIndex).ToListAsync(cancellationToken);
    var inputs = claims.Select(item => new ClaimInput(item.Id, item.ParticipantId, item.AchievementCode, item.SeasonIndex,
        item.ClubName, item.CountryCode, item.CreatedAt, ClubId: item.ClubId,
        HasAttestedClubTrophy: RaceScoringEngine.HasAttestedClubTrophy(item))).ToArray();
    return RaceScoringEngine.ValidateFirstMajorClubTrophy(new ClaimInput(Guid.Empty, participantId, "G040", evidence.SeasonIndex,
        evidence.ClubName, evidence.CountryCode, DateTimeOffset.UtcNow, ClubId: evidence.ClubId), inputs);
}

static async Task<string?> ValidateDomesticDoubleConsistencyAsync(
    ChallengeDbContext database, Guid participantId, Guid? excludingClaimId,
    ICompetitionEvidence evidence, CancellationToken cancellationToken)
{
    var claims = (await database.AchievementClaims.Where(item => item.ParticipantId == participantId
        && item.SeasonIndex == evidence.SeasonIndex && item.Id != excludingClaimId
        && (item.AchievementCode == "G001" || item.AchievementCode == "G008"))
        .ToListAsync(cancellationToken))
        .Select(item => new ClaimInput(item.Id, item.ParticipantId, item.AchievementCode, item.SeasonIndex,
            item.ClubName, item.CountryCode, item.CreatedAt, ClubId: item.ClubId)).ToArray();
    return RaceScoringEngine.ValidateDomesticDoubleConsistency(new ClaimInput(
        Guid.Empty, participantId, "G013", evidence.SeasonIndex, evidence.ClubName, evidence.CountryCode,
        DateTimeOffset.UtcNow, ClubId: evidence.ClubId,
        WonFirstDivisionTitle: evidence.WonFirstDivisionTitle,
        WonMajorNationalCup: evidence.WonMajorNationalCup), claims);
}

static Dictionary<string, string[]> ValidateCompetitionParticipation(string code, ICompetitionEvidence evidence, int minimumCupMatches = 2)
{
    var errors = new Dictionary<string, string[]>();
    var managedMatches = evidence.ManagedCompetitionMatches;
    var totalMatches = evidence.TotalCompetitionMatches;
    var inChargeAtEnd = evidence.InChargeAtCompetitionEnd;
    var managedDecidingMatch = evidence.ManagedDecidingMatch;
    var firstSeasonAfterPromotion = evidence.FirstSeasonAfterPromotion;
    var avoidedRelegation = evidence.AvoidedRelegation;
    var leaguePosition = evidence.CompetitionLeaguePosition;
    if (code is "G001" or "G002" or "G003" or "G004" or "G011" or "G013" or "G014" or "G019")
    {
        if (totalMatches is null or < 1 or > 1000)
            errors["totalCompetitionMatches"] = ["Enter the total number of league matches, between 1 and 1000."];
        if (managedMatches is null or < 1 or > 1000 || totalMatches is > 0 && managedMatches > totalMatches)
            errors["managedCompetitionMatches"] = ["Enter a valid number of league matches managed."];
        else if (totalMatches is > 0 && (long)managedMatches.Value * (code == "G011" ? 2 : 3) < totalMatches.Value)
            errors["managedCompetitionMatches"] = [code == "G011"
                ? "You must manage at least half of the league matches."
                : "You must manage at least one third of the league matches."];
        if (code != "G011" && inChargeAtEnd != true)
            errors["inChargeAtCompetitionEnd"] = ["You must be in charge at the end of the league competition."];
    }
    else if (code is "G008" or "G009" or "G015")
    {
        if (managedMatches is null or > 1000 || managedMatches < minimumCupMatches)
            errors["managedCompetitionMatches"] = [$"You must manage at least {minimumCupMatches} matches in this cup."];
        if (managedDecidingMatch != true)
            errors["managedDecidingMatch"] = ["You must manage the cup final."];
    }
    else if (code is "G010" or "G023")
    {
        if (managedDecidingMatch != true)
            errors["managedDecidingMatch"] = ["You must manage the winning super cup match."];
    }
    if (code is "G011" or "G016" or "G019" && firstSeasonAfterPromotion != true)
        errors["firstSeasonAfterPromotion"] = ["This must be the club's first season after promotion to the first division."];
    if (code == "G011" && avoidedRelegation != true)
        errors["avoidedRelegation"] = ["The club must remain in the first division."];
    if (code == "G012" && leaguePosition != 2)
        errors["competitionLeaguePosition"] = ["First-division runner-up requires a final position of 2."];
    if (code == "G016" && leaguePosition is not (>= 2 and <= 4))
        errors["competitionLeaguePosition"] = ["A newly promoted top-four finish requires a final position between 2 and 4."];
    if (code == "G019" && leaguePosition != 1)
        errors["competitionLeaguePosition"] = ["First-division champion requires a final position of 1."];
    if (code == "G013")
    {
        if (string.IsNullOrWhiteSpace(evidence.CountryCode))
            errors["countryCode"] = ["Specify the country where both trophies were won."];
        if (evidence.WonFirstDivisionTitle != true || evidence.WonMajorNationalCup != true)
            errors["domesticDouble"] = ["The domestic double requires both the first-division title and major national cup in the same country and season."];
        if (evidence.DomesticCupMatchesManaged is null or < 2 or > 1000)
            errors["domesticCupMatchesManaged"] = ["You must manage at least two matches in the major national cup."];
        if (managedDecidingMatch != true)
            errors["managedDecidingMatch"] = ["You must manage the cup final."];
    }
    if (code == "G014" && evidence.WonLeagueTitleUnbeaten != true)
        errors["wonLeagueTitleUnbeaten"] = ["The unbeaten league bonus requires winning the league without a defeat."];
    if (code == "G015" && evidence.CupWonInLowerDivision != true)
        errors["cupWonInLowerDivision"] = ["The major national cup must be won with a lower-division club."];
    if (code is "G005" or "G006" or "G007")
    {
        var matchesDestination = code switch
        {
            "G005" => evidence.PromotionDestinationLevel == 1,
            "G006" => evidence.PromotionDestinationLevel == 2,
            _ => evidence.PromotionDestinationLevel is >= 3 and <= 100,
        };
        if (!matchesDestination)
            errors["promotionDestinationLevel"] = ["Enter the division level reached by this promotion."];
        if (evidence.PromotionWithoutTitle != true)
            errors["promotionWithoutTitle"] = ["This promotion award requires promotion without the corresponding league title."];
    }
    if (code is "G017" or "G018")
    {
        var requiredSeasons = code == "G017" ? 2 : 3;
        if (evidence.PromotionStreakStartSeason is null or < 0
            || (long)evidence.SeasonIndex - evidence.PromotionStreakStartSeason.Value + 1 != requiredSeasons)
            errors["promotionStreakStartSeason"] = [$"The first promotion must be {requiredSeasons - 1} consecutive season(s) before this milestone."];
    }
    if (code is "G025" or "G026" or "G027")
    {
        if (evidence.ReachedContinentalMainPhase != true)
            errors["reachedContinentalMainPhase"] = ["Continental participation requires reaching the main group or league phase."];
        if (code == "G025" && evidence.PriorContinentalMainPhase != false)
            errors["priorContinentalMainPhase"] = ["Confirm that this is the club's first continental main-phase participation."];
        if (code == "G026" && evidence.PriorContinentalMainPhase != true)
            errors["priorContinentalMainPhase"] = ["Confirm that the club previously reached a continental main phase."];
        if (code == "G027" && evidence.FirstTopTierMainPhase != true)
            errors["firstTopTierMainPhase"] = ["Confirm that this is the club's first main-phase participation in the top-tier continental competition."];
    }
    if (code == "G032")
    {
        if (string.IsNullOrWhiteSpace(evidence.ClubName) || string.IsNullOrWhiteSpace(evidence.CountryCode))
            errors["clubName"] = ["Enter the second club and its country code."];
        if (string.IsNullOrWhiteSpace(evidence.FirstContinentalTitleClub) || evidence.FirstContinentalTitleClub.Length > 120
            || string.IsNullOrWhiteSpace(evidence.FirstContinentalTitleCountry) || evidence.FirstContinentalTitleCountry.Length > 8)
            errors["firstContinentalTitleClub"] = ["Enter the first club and its country code."];
        if ((evidence.ClubId.HasValue && evidence.FirstContinentalTitleClubId.HasValue
                ? evidence.ClubId == evidence.FirstContinentalTitleClubId
                : Same(evidence.ClubName, evidence.FirstContinentalTitleClub) && Same(evidence.CountryCode, evidence.FirstContinentalTitleCountry)))
            errors["firstContinentalTitleClub"] = ["The two continental titles must be won with different clubs."];
        if (evidence.FirstContinentalTitleSeason is null or < 0 || evidence.FirstContinentalTitleSeason > evidence.SeasonIndex)
            errors["firstContinentalTitleSeason"] = ["The first title season must be between season 0 and the milestone season."];
        if (evidence.WonContinentalTitlesWithBothClubs != true)
            errors["wonContinentalTitlesWithBothClubs"] = ["Confirm that you won a continental club trophy with each club."];
    }
    if (code == "G038" && evidence.FirstDivisionExperience != true)
        errors["firstDivisionExperience"] = ["Confirm that this is your first experience managing in the first division."];
    if (code == "G039")
    {
        if (evidence.HigherDivisionClubMove != true)
            errors["higherDivisionClubMove"] = ["Confirm that this was your first move to a club in a division higher than any you previously managed."];
        if (evidence.PreviousBestDivisionLevel is null or < 2 or > 100)
            errors["previousBestDivisionLevel"] = ["Enter the best division previously managed, between 2 and 100."];
        if (evidence.NewDivisionLevel is null or < 1 or > 99
            || evidence.PreviousBestDivisionLevel is int previous && evidence.NewDivisionLevel >= previous)
            errors["newDivisionLevel"] = ["Enter a higher division than your previous best (a lower division number)."];
    }
    if (code == "G040")
    {
        if (evidence.FirstMajorClubTrophy != true)
            errors["firstMajorClubTrophy"] = ["Confirm that this is your first major trophy with this club."];
        if (evidence.TrophyNotSuperCup != true)
            errors["trophyNotSuperCup"] = ["Super cups do not qualify for this achievement."];
    }
    if (code == "G050")
    {
        if (evidence.ThirdDivisionStartSeason is null or < 0 || evidence.ThirdDivisionStartSeason > evidence.SeasonIndex)
            errors["thirdDivisionStartSeason"] = ["Enter the season when you managed this club in the third division, no later than the milestone season."];
        if (evidence.StayedWithClubFromThirdToFirst != true)
            errors["stayedWithClubFromThirdToFirst"] = ["Confirm that you stayed with the same club from the third division until reaching the first."];
    }
    if (code == "G048" && evidence.WonTenMajorTrophies != true)
        errors["wonTenMajorTrophies"] = ["Confirm that you have won ten major trophies by this season."];
    if (code == "G051" && evidence.OfficialManagerOfYear != true)
        errors["officialManagerOfYear"] = ["Confirm that this is the official Football Manager Manager of the Year award."];
    if (CompetitionResultRequirement(code) is { } result)
    {
        if (evidence.CompetitionResultKind != result.Kind)
            errors["competitionResultKind"] = ["Select the competition type required by this achievement."];
        if (evidence.CompetitionResultStage != result.Stage)
            errors["competitionResultStage"] = ["Select the result required by this achievement."];
    }
    return errors;
}

static (string Kind, string Stage)? CompetitionResultRequirement(string code) => code switch
{
    "G020" => ("ClubContinentalTop", "Winner"),
    "G021" => ("ClubContinentalSecond", "Winner"),
    "G022" => ("ClubContinentalThird", "Winner"),
    "G024" => ("ClubWorld", "Winner"),
    "G028" => ("ClubContinentalTop", "QuarterFinalElimination"),
    "G029" => ("ClubContinentalTop", "SemiFinalElimination"),
    "G030" => ("ClubContinentalTop", "RunnerUp"),
    "G031" => ("ClubContinentalSecond", "RunnerUp"),
    "G034" => ("NationalWorld", "Winner"),
    "G035" => ("NationalContinental", "Winner"),
    "G036" => ("NationalWorld", "SemiFinalElimination"),
    _ => null,
};

static async Task<bool> HasConflictingContinentalParticipationAsync(
    ChallengeDbContext database, Guid participantId, Guid? excludingClaimId, string code, string? clubName,
    string? countryCode, int seasonIndex, CancellationToken cancellationToken)
{
    if (code is not ("G025" or "G026") || string.IsNullOrWhiteSpace(clubName)) return false;
    var otherCode = code == "G025" ? "G026" : "G025";
    var otherClaims = await database.AchievementClaims
        .Where(item => item.ParticipantId == participantId && item.Id != excludingClaimId && item.AchievementCode == otherCode)
        .Select(item => new { item.ClubName, item.CountryCode, item.SeasonIndex }).ToListAsync(cancellationToken);
    return otherClaims.Any(item => SameRecordedClub(item.ClubName, item.CountryCode, clubName, countryCode)
        && (code == "G025" ? item.SeasonIndex <= seasonIndex : item.SeasonIndex >= seasonIndex));
}

static string FrequencyConflictMessage(AchievementFrequency frequency) =>
    frequency is AchievementFrequency.PerClub or AchievementFrequency.ClubSeason
        ? "This achievement has already been recorded at its allowed frequency. If clubs share a name and country, link the earlier claim to its catalog club to distinguish them."
        : "This achievement has already been recorded at its allowed frequency.";

static string GetOccurrenceKey(
    AchievementFrequency frequency,
    int seasonIndex,
    string? clubName,
    string? countryCode)
{
    var occurrence = frequency switch
    {
        AchievementFrequency.Once => "once",
        AchievementFrequency.Season => $"season:{seasonIndex}",
        AchievementFrequency.PerClub => $"club:{clubName?.Trim().ToUpperInvariant()}",
        AchievementFrequency.PerCountry => $"country:{countryCode?.Trim().ToUpperInvariant()}",
        AchievementFrequency.ClubSeason => $"club-season:{clubName?.Trim().ToUpperInvariant()}:{seasonIndex}",
        _ => throw new ArgumentOutOfRangeException(nameof(frequency)),
    };
    if (frequency is not (AchievementFrequency.PerClub or AchievementFrequency.ClubSeason)) return occurrence;
    static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    return $"club-v2:{Encode(occurrence)}:{Encode(countryCode?.Trim().ToUpperInvariant() ?? string.Empty)}";
}

static bool SameRecordedClub(string? firstName, string? firstCountry, string? secondName, string? secondCountry) =>
    RaceScoringEngine.SameRecordedClub(firstName, firstCountry, secondName, secondCountry);

static bool Same(string? left, string? right) =>
    !string.IsNullOrWhiteSpace(left)
    && !string.IsNullOrWhiteSpace(right)
    && left.Trim().Equals(right.Trim(), StringComparison.OrdinalIgnoreCase);

public sealed record ChallengeCreateRequest(string? Name, string? Description, int StartSeasonYear = 2026,
    string? TrainerName = null, string[]? AchievementCodes = null, bool SaveAsDraft = false);
public sealed record PublishDraftRequest(long? Version);
public sealed record ProfileUpdateRequest(string? DisplayName, string? AvatarUrl);
public sealed record InviteCreateRequest(string? Email, int? ExpiresInDays = null);
public sealed record JoinChallengeRequest(string? Token);
public interface ICompetitionEvidence
{
    string? ClubName { get; }
    Guid? ClubId { get; }
    bool ClearClubId { get; }
    string? CountryCode { get; }
    int SeasonIndex { get; }
    int? ManagedCompetitionMatches { get; }
    int? TotalCompetitionMatches { get; }
    bool? InChargeAtCompetitionEnd { get; }
    bool? ManagedDecidingMatch { get; }
    bool? FirstSeasonAfterPromotion { get; }
    bool? AvoidedRelegation { get; }
    int? CompetitionLeaguePosition { get; }
    bool? WonFirstDivisionTitle { get; }
    bool? WonMajorNationalCup { get; }
    bool? WonLeagueTitleUnbeaten { get; }
    bool? CupWonInLowerDivision { get; }
    int? DomesticCupMatchesManaged { get; }
    int? PromotionDestinationLevel { get; }
    bool? PromotionWithoutTitle { get; }
    int? PromotionStreakStartSeason { get; }
    bool? ReachedContinentalMainPhase { get; }
    bool? PriorContinentalMainPhase { get; }
    bool? FirstTopTierMainPhase { get; }
    string? FirstContinentalTitleClub { get; }
    string? FirstContinentalTitleCountry { get; }
    Guid? FirstContinentalTitleClubId { get; }
    bool ClearFirstContinentalTitleClubId { get; }
    int? FirstContinentalTitleSeason { get; }
    bool? WonContinentalTitlesWithBothClubs { get; }
    string? StartingCareerClub { get; }
    string? StartingCareerCountry { get; }
    Guid? StartingCareerClubId { get; }
    bool ClearStartingCareerClubId { get; }
    bool? FirstDivisionExperience { get; }
    bool? HigherDivisionClubMove { get; }
    int? PreviousBestDivisionLevel { get; }
    int? NewDivisionLevel { get; }
    bool? FirstMajorClubTrophy { get; }
    bool? TrophyNotSuperCup { get; }
    bool? WonTenMajorTrophies { get; }
    int? ThirdDivisionStartSeason { get; }
    bool? StayedWithClubFromThirdToFirst { get; }
    bool? OfficialManagerOfYear { get; }
    string? CompetitionResultKind { get; }
    string? CompetitionResultStage { get; }
    Guid? CompetitionId { get; }
}
public sealed record ClaimCreateRequest(string Code, int SeasonIndex, string? ClubName, string? CountryCode, string? Notes,
    int? ManagedCompetitionMatches, int? TotalCompetitionMatches, bool? InChargeAtCompetitionEnd, bool? ManagedDecidingMatch,
    bool? FirstSeasonAfterPromotion, bool? AvoidedRelegation, int? CompetitionLeaguePosition,
    bool? WonFirstDivisionTitle, bool? WonMajorNationalCup, bool? WonLeagueTitleUnbeaten, bool? CupWonInLowerDivision, int? DomesticCupMatchesManaged,
    int? PromotionDestinationLevel, bool? PromotionWithoutTitle, int? PromotionStreakStartSeason,
    bool? ReachedContinentalMainPhase, bool? PriorContinentalMainPhase, bool? FirstTopTierMainPhase,
    string? FirstContinentalTitleClub, string? FirstContinentalTitleCountry, int? FirstContinentalTitleSeason,
    bool? WonContinentalTitlesWithBothClubs, string? StartingCareerClub, string? StartingCareerCountry, Guid? CompetitionId,
    bool? FirstDivisionExperience, bool? HigherDivisionClubMove, int? PreviousBestDivisionLevel, int? NewDivisionLevel,
    bool? FirstMajorClubTrophy, bool? TrophyNotSuperCup, bool? WonTenMajorTrophies, int? ThirdDivisionStartSeason,
    bool? StayedWithClubFromThirdToFirst, bool? OfficialManagerOfYear,
    string? CompetitionResultKind, string? CompetitionResultStage, Guid? ClubId = null, bool ClearClubId = false,
    Guid? StartingCareerClubId = null, bool ClearStartingCareerClubId = false,
    Guid? FirstContinentalTitleClubId = null, bool ClearFirstContinentalTitleClubId = false) : ICompetitionEvidence;
public sealed record ClaimUpdateRequest(int SeasonIndex, string? ClubName, string? CountryCode, string? Notes, long Version,
    int? ManagedCompetitionMatches, int? TotalCompetitionMatches, bool? InChargeAtCompetitionEnd, bool? ManagedDecidingMatch,
    bool? FirstSeasonAfterPromotion, bool? AvoidedRelegation, int? CompetitionLeaguePosition,
    bool? WonFirstDivisionTitle, bool? WonMajorNationalCup, bool? WonLeagueTitleUnbeaten, bool? CupWonInLowerDivision, int? DomesticCupMatchesManaged,
    int? PromotionDestinationLevel, bool? PromotionWithoutTitle, int? PromotionStreakStartSeason,
    bool? ReachedContinentalMainPhase, bool? PriorContinentalMainPhase, bool? FirstTopTierMainPhase,
    string? FirstContinentalTitleClub, string? FirstContinentalTitleCountry, int? FirstContinentalTitleSeason,
    bool? WonContinentalTitlesWithBothClubs, string? StartingCareerClub, string? StartingCareerCountry, Guid? CompetitionId,
    bool? FirstDivisionExperience, bool? HigherDivisionClubMove, int? PreviousBestDivisionLevel, int? NewDivisionLevel,
    bool? FirstMajorClubTrophy, bool? TrophyNotSuperCup, bool? WonTenMajorTrophies, int? ThirdDivisionStartSeason,
    bool? StayedWithClubFromThirdToFirst, bool? OfficialManagerOfYear,
    string? CompetitionResultKind, string? CompetitionResultStage, Guid? ClubId = null, bool ClearClubId = false,
    Guid? StartingCareerClubId = null, bool ClearStartingCareerClubId = false,
    Guid? FirstContinentalTitleClubId = null, bool ClearFirstContinentalTitleClubId = false) : ICompetitionEvidence;
public sealed record ChallengeCorrectionRequest(bool Confirm);
public sealed record TransferRequest(int SeasonIndex, string? ClubName, string? PlayerName,
    string? CounterpartyName, string? Direction, decimal FixedAmount, string? Currency, string? Notes, long? Version);
public sealed record NationalTeamRequest(string? TeamName, int StartSeasonIndex, int? EndSeasonIndex,
    int OfficialMatches, string? Notes, long? Version, string? CountryCode = null,
    IReadOnlyList<NationalTeamSeasonMatchRequest>? SeasonMatches = null);
public sealed record NationalTeamSeasonMatchRequest(int SeasonIndex, int OfficialMatches);
public sealed record NationalTeamMilestoneRequest(int? SeasonIndex, long Version);
public sealed record CareerStartRequest(string? Status, Guid? StintId, long Version);
public sealed record CountryMilestoneRequest(int? SeasonIndex, long? Version);
public sealed record ClubCatalogRequest(string? Name, string? CountryCode, string? IdentityLabel = null);
public sealed record RuleChangeRequest(string? Code, int Points, bool IsEnabled, string? Policy,
    int EffectiveSeasonIndex, int ExpectedVersion, long? ExpectedChallengeVersion, bool Confirm, string? Reason,
    string? NameOverride = null, string? DescriptionOverride = null, string? CategoryOverride = null,
    string? FrequencyOverride = null, int? MinimumCupMatchesOverride = null);
public sealed record CustomAchievementRequest(string? Name, string? Description, string? Frequency, int Points,
    int EffectiveSeasonIndex, int ExpectedVersion, long? ExpectedChallengeVersion);
public sealed record CareerStintCreateRequest(
    string ClubName,
    string CountryCode,
    string CountryName,
    int StartSeasonIndex,
    int? EndSeasonIndex,
    int InitialDivisionLevel,
    int OfficialMatches,
    int? EligibleSeasonIndex,
    long? Version = null,
    Guid? ClubId = null,
    bool ClearClubId = false);
public sealed record CareerMatchesUpdateRequest(int OfficialMatches, int? EligibleSeasonIndex, long? Version);
public sealed record CompetitionRequest(string? Name, string? Tier, long? Version);
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
    bool IsCompleted,
    long? Version,
    bool ConfirmTransferCurrencyRelabel,
    bool? UsesClubBreakdown = null,
    ClubSeasonStatisticsRequest[]? ClubSeasons = null,
    bool ClearClubBreakdown = false);
public sealed record ClubSeasonStatisticsRequest(Guid CareerStintId, int DivisionLevel, int OfficialMatches,
    int LeagueMatches, int LeagueWins, int LeagueDraws, int LeagueLosses, int LeaguePoints,
    int GoalsFor, int GoalsAgainst, int? LeaguePosition);

public partial class Program;
