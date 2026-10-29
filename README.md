# FM Career Challenge — Backend

Independent ASP.NET Core 10 REST API and SignalR hub. PostgreSQL is the source of truth for identities, challenge records, achievement claims, score events, notifications, and audit history.

## Requirements

- .NET 10 SDK
- Docker Desktop with Docker Compose (recommended for local PostgreSQL)

## Local development

Start PostgreSQL and the API:

```powershell
docker compose up --build
```

The API listens at `http://localhost:5195`; health is available at `/health`. OpenAPI is available at `/openapi/v1.json` in Development. Configure Google and Microsoft OAuth credentials with environment variables before using those sign-in buttons:

```powershell
$env:Authentication__Google__ClientId = "..."
$env:Authentication__Google__ClientSecret = "..."
$env:Authentication__Microsoft__ClientId = "..."
$env:Authentication__Microsoft__ClientSecret = "..."
dotnet run --urls http://localhost:5195
```

The local development database defaults to `postgres/postgres` on port 5432. Override `ConnectionStrings__PostgreSql` for another PostgreSQL instance. Never commit actual credentials.

OAuth callback URLs:

- Google: `https://your-api-host/signin-google`
- Microsoft: `https://your-api-host/signin-microsoft`

Set the provider redirect URI to the API callback and `Frontend__BaseUrl` to the public frontend origin. The production reverse proxy should serve frontend and API on one HTTPS origin and allow WebSocket upgrades for `/hubs/challenges`.

## Security and application behavior

- External OAuth logins use ASP.NET Core Identity's external cookie flow; application sessions are HttpOnly cookies.
- Unsafe `/api` requests require the antiforgery token returned by `GET /api/auth/csrf`.
- Challenge and SignalR group membership are authorized against persisted participant records.
- Invite secrets are random, stored only as SHA-256 hashes, and expire after seven days.
- Score projections are rebuilt deterministically from claims, season records, and rules; competition order uses the in-game season index.
- The default challenge contains the 53 stable G001–G053 achievement codes.
- Provider credentials are intentionally unset. The API returns a clear 503 response when a provider is not configured.

## Independent repository setup

This folder is the backend project root and can be initialized and published independently of `frontend/`. It does not depend on a root solution file or shared package manifest.
