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

The API listens at `http://localhost:5195`; health is available at `/health`. OpenAPI is available at `/openapi/v1.json` in Development. Configure Google OAuth credentials before using Google sign-in:

```powershell
$env:Authentication__Google__ClientId = "..."
$env:Authentication__Google__ClientSecret = "..."
dotnet run --urls http://localhost:5195
```

The local development database defaults to `postgres/postgres` on port 5432. Override `ConnectionStrings__PostgreSql` for another PostgreSQL instance. Never commit actual credentials.

Development startup applies the EF Core migrations automatically, using a PostgreSQL advisory lock so concurrent application initializers wait for each other. For other environments, apply migrations as a single explicit deployment step using an appropriately privileged database account; the EF CLI does not use the application startup lock:

```powershell
dotnet ef database update
```

Set `ConnectionStrings__PostgreSql` for the target database first. Back up existing data before applying migrations. Do not rely on Development startup behavior in production.

Run the scoring unit tests with:

```powershell
dotnet test tests\FmCareerChallenge.UnitTests\FmCareerChallenge.UnitTests.csproj
```

The PostgreSQL integration tests use a disposable Testcontainers database and require Docker:

```powershell
dotnet test tests\FmCareerChallenge.IntegrationTests\FmCareerChallenge.IntegrationTests.csproj
```

Alternatively, set `FMCC_TEST_POSTGRES` to a dedicated disposable PostgreSQL database before running those tests. The tests apply migrations and insert data, so never point this variable at a production or shared database.

For frontend browser tests, create a separate PostgreSQL database whose name begins with `fmcc_e2e_`, then run:

```powershell
$env:FMCC_E2E_POSTGRES = "Host=127.0.0.1;Port=5432;Database=fmcc_e2e_browser;Username=your_test_user;Password=your_test_password"
dotnet run --project tests\FmCareerChallenge.E2EHost\FmCareerChallenge.E2EHost.csproj --no-launch-profile
```

This separate test host listens only on `127.0.0.1:5196`, migrates the designated database and creates two fresh test identities for each browser journey. It adds data without clearing existing records. Never use a production or shared database, expose the host to other machines, or deploy it. It does not test OAuth. With the host running, follow the frontend README to run the browser tests.

OAuth callback URLs:

- Local Google development: `http://localhost:5195/signin-google`
- Production Google sign-in: `https://your-api-host/signin-google`

Register the Google callback URL as an authorized redirect URI in Google Cloud, and set `Frontend__BaseUrl` to the frontend origin. The provider callback is `/signin-google`; after Google validates the response, the API completes its local sign-in at `/api/auth/external-callback`. New accounts are accepted only when Google returns a verified email address. Configure credentials through environment variables or a secret manager; never commit them. The production reverse proxy should serve frontend and API on one HTTPS origin and allow WebSocket upgrades for `/hubs/challenges`.

When TLS terminates at a reverse proxy, configure each **immediate proxy's IP address** using `ReverseProxy__KnownProxies__0`, `ReverseProxy__KnownProxies__1`, and so on. By default no forwarded headers are trusted, including those from loopback. The API accepts only one hop of `X-Forwarded-Proto` and `X-Forwarded-For` from these addresses; it ignores `X-Forwarded-Host`. Configure the proxy to overwrite or safely append the client address, preserve the correct public `Host`, reject unexpected hosts, and prevent clients from directly reaching the API. Configure `AllowedHosts` for the expected API host. Do not enable the framework's unrestricted forwarded-header shortcut.

Outside Development, startup requires explicit `AllowedHosts` and Data Protection settings. Set `AllowedHosts` to a semicolon-separated list of expected host names (not `*`), and configure:

```powershell
$env:AllowedHosts = "api.example.com"
$env:DataProtection__ApplicationName = "fm-career-challenge"
$env:DataProtection__KeyRingPath = "C:\secure\fmcc-keys"
$env:DataProtection__CertificatePath = "C:\secure\fmcc-key-encryption.pfx"
$env:DataProtection__CertificatePassword = "<provided by your secret manager>"
```

Use absolute paths to an existing, writable key-ring directory and a PKCS#12 certificate containing its RSA private key. Store the certificate and password securely and separately from the key ring; do not commit them. All instances must share the same durable key-ring directory, active certificate and application name. Restrict key-ring write access to trusted API instances: encryption does not protect against a writer replacing keys. Protect and back up the ring and certificates together; losing an encryption certificate invalidates cookies and logins protected by keys encrypted with it.

For certificate rotation, make the new certificate active and retain each previous certificate (including its private key) for decrypting existing keys:

```powershell
$env:DataProtection__PreviousCertificates__0__Path = "C:\secure\fmcc-key-encryption-previous.pfx"
$env:DataProtection__PreviousCertificates__0__Password = "<provided by your secret manager>"
```

Add further previous certificates with increasing indexes. Keep them available for every instance until you deliberately retire the associated keys and accept that sessions protected by them will be invalidated. Test the rotation with the deployed key-ring storage before applying it. Outside Development, EF CLI commands that start the application also need these settings.

## Fly.io backend configuration

`fly.toml` targets `road-to-500-api-prod` in Frankfurt (`fra`), near the Neon AWS `eu-central-1` region. It builds the existing Dockerfile and runs the production API on port 8080 without a public service. The frontend proxy must connect to `road-to-500-api-prod.internal:8080` over Fly's private IPv6 network, preserve the public Host, and forward API, Google callback and SignalR requests. Deploy both apps in the same Fly organization and private network.

The configuration mounts a volume named `fmcc_keys` at `/data`, which is also the Data Protection key-ring directory. Fly supplies the existing mount directory before application startup. The Base64-encoded `FMCC_DATA_PROTECTION_PFX_BASE64` secret is decoded to `/app/data-protection-prod.pfx` at boot; `DataProtection__CertificatePassword` unlocks it. No credential values belong in `fly.toml`.

Required staged secrets:

- `ConnectionStrings__PostgreSql`
- `Authentication__Google__ClientId`
- `Authentication__Google__ClientSecret`
- `DataProtection__CertificatePassword`
- `FMCC_DATA_PROTECTION_PFX_BASE64`

The public origin is `https://road-to-500-web-prod.fly.dev`. The Google redirect URI is `https://road-to-500-web-prod.fly.dev/signin-google`. The health check explicitly sends this public Host to pass host filtering. `/health` is a process liveness check, not a database, migration or OAuth readiness check. Top-level Fly checks monitor the private API but do not route traffic.

Validate the configuration from this repository without deploying:

```powershell
fly config validate --strict
```

**Do not deploy until the remaining prerequisites are complete:**

- Configure the frontend proxy and set `ReverseProxy__KnownProxies__0` (and further indexes if needed) to its verified immediate source IPs. The current file trusts the deployed frontend Machine's verified private IPv6 address. Recheck and update this address when replacing or moving that Machine; do not substitute a guessed IP or enable unrestricted forwarding.
- Provision one `fmcc_keys` volume in `fra` after approving the storage cost, and keep an independent secure backup of the key ring, certificate and password. A volume is local to one Machine and is not a shared key ring.
- Fly runs `FmCareerChallenge.Api.dll --migrate` as an explicit release command before updating the API. This mode requires a direct Neon connection and exits after applying migrations under the existing advisory lock, without starting the web server or requiring the key-ring volume. Back up existing production data before deployment; release Machines do not mount persistent volumes.
- Use exactly one API Machine initially. At the authorized first deploy, pass `--ha=false` to prevent Fly from creating a redundant Machine. The TOML does not enforce a replica count. Do not scale out until shared Data Protection storage and SignalR scale-out are implemented.

This private-network configuration has no Fly Proxy autostop/autostart service. Keep the API running for SignalR; a stopped Machine will not wake through `.internal` requests. The initial size is one shared CPU with 512 MB RAM, to be adjusted from observed usage. A single Machine has downtime during rolling updates and is not highly available.

## Security and application behavior

- External OAuth logins use ASP.NET Core Identity's external cookie flow; application sessions are HttpOnly cookies.
- Unsafe `/api` requests require the antiforgery token returned by `GET /api/auth/csrf`.
- Challenge and SignalR group membership are authorized against persisted participant records.
- Requests are limited per authenticated user, or per connection IP address for anonymous requests. Behind a configured trusted proxy, the validated forwarded client address is used; without one, clients behind the same proxy share a limit.
- `POST /api/challenges` accepts `saveAsDraft` to create a configuration-only draft. Rules and competition settings remain editable, while gameplay and invitations are blocked. The administrator publishes it with `POST /api/challenges/{id}/publish` and the current challenge `version`; publishing is audited and transitions to `PendingPlayers`. Omitting `saveAsDraft` preserves the existing immediate-publish behavior.
- New accounts complete profile onboarding by choosing a display name and, optionally, an HTTPS avatar URL. Existing accounts are marked as onboarded by the migration. `PUT /api/me` also supports later profile edits.
- New careers must declare an unemployed start or a starting club below the first division before entering results. One initial club stint can be entered first to identify a club start; the user may instead declare an unemployed start and later manage a first-division club. A declared starting club must be in the earliest recorded season; if several stints start in that season, the player selects the actual starting club. The declaration is self-reported, versioned and audited, and protected against incompatible stint edits. Existing careers are not forced to supply missing historical evidence, but can record or correct it. Existing club/country move claims must agree with a declared starting club.
- Invite secrets are random and stored only as SHA-256 hashes. Administrators can set a lifetime from one to 30 days (seven by default), view invitation status and revoke pending invitations; invited users can accept or decline email invitations. Changes are audited, and acceptance, decline and revocation are serialized.
- Score projections are rebuilt deterministically from claims, season records, and rules; competition order uses the in-game season index.
- Challenge administrators can version the points and enabled state of first bonuses, annual duels and fixed checkpoints through the same preview, audit and effective-season controls as achievements. A first bonus or checkpoint disabled when it is first reached is not awarded later merely because it was re-enabled. Older rule versions without these settings retain the original defaults.
- Achievement names, descriptions and categories can also be customized per challenge and rule version. These are display text only and do not change official scoring conditions. The achievement list displays the latest configured text, including scheduled versions; score breakdown labels use the version applicable to each event. Omitted text fields preserve the existing value at each season boundary. To restore catalog wording, enter that wording explicitly.
- Administrators can version the supported frequency of manual achievements, with an impact preview before applying. Each claim uses the frequency effective in its season, considering earlier eligible records even when their frequency differed. Earlier records can therefore prevent a later once-per-challenge or per-club award; a later less restrictive frequency can allow further awards. The preview recalculates existing claims and later scheduled versions remain intact when an older client omits the frequency field. Automatically calculated milestones and G037 retain their fixed semantics.
- The minimum cup matches managed can be versioned for G008, G009 and G015 (default two; range 1–1,000). The manager must still have managed the final. The preview and recalculation remove points and first-bonus eligibility from attested claims below the effective minimum; such claims do not consume frequency. Legacy claims with no match count remain scored and are identified as lacking evidence. Other cup conditions are unchanged.
- `GET /api/challenges/{id}/scores` includes individual scoring events, their rule version, status and applicable pending or exclusion explanations, as well as a pending-victory indicator for active challenges. Only challenge participants can access this breakdown. Checkpoints remain pending when an earlier or same-season score event is unresolved.
- Season updates use the version returned by `GET /api/challenges/{id}/seasons`; provide `null` for a new season. Stale versions return HTTP 409. Reopening or editing a season records an audit entry and recalculates scoring.
- Seasons support either a quick league summary or a per-stint breakdown. A detailed breakdown aggregates club league matches, results, points and goals for duels without adding the quick summary. Stints must belong to the manager, cover the season and have enough total official matches to support allocations across seasons. Switching back to quick mode retains detailed records unless explicitly cleared; deleting or shortening a stint is blocked while its season records would be lost or invalidated. Historical clients cannot overwrite an active detailed season without explicitly choosing a mode. When active detailed records account for every official match in all relevant stints, club and country 20-match seasons are calculated from those records; otherwise the attested milestone remains the fallback. The underlying match records are self-reported.
- Achievement corrections and removals are restricted to their owner and require the version returned by the achievements endpoint. Both changes are audited and recalculate scores.
- New and corrected league-title claims (G001–G004) require self-attested participation in at least one third of league matches and confirmation that the manager remained in charge at the end. National and league cup claims (G008–G009) require the configured minimum number of matches, including the final; super cup claims (G010, G023) require managing the winning match. Pre-existing claims remain scored without retroactive data loss and are marked as lacking participation details; correcting one requires the new information.
- New and corrected G040 claims require confirmation of a first major club trophy and that it was not a super cup; G050 requires a third-division starting season and confirmation of continuous progression with the same club; G051 requires confirmation of the official FM Manager of the Year award. These are self-attested, not independently verified or cross-checked against complete trophy and division records. Older claims retain their points and are identified as missing evidence.
- New and corrected continental and international result claims G020–G022, G024, G028–G031 and G034–G036 require the competition type and achieved result. These are player attestations, not independently verified competition results; older records keep their points and are marked as missing evidence.
- G048 is automatically calculated once ten qualifying major trophy claims have been recorded, using the same classification as the victory tiebreak. If some trophies are not recorded as enabled objective claims, a player may instead attest the milestone manually. An earlier manual milestone remains in effect; an automatic and manual record never stack.
- G011, G016 and G019 require attestation that the club is in its first first-division season after promotion; survival also requires at least half the league matches and confirmation the club stayed up. G012, G016 and G019 require the appropriate final league position. G019 also requires the league-title participation criteria.
- G013 requires both domestic titles in the same stated country and season, with league and cup participation. If same-season G001/G008 claims exist, known club and country identities must agree; conflicting new claims are rejected, and later corrections can remove G013 points. Missing supporting claims do not invalidate the attested double, and legacy claims without modern attestations keep their historical score. G014 requires an unbeaten league title and G015 a major cup won by a lower-division club; these remain self-attested facts.
- G025-G027 require self-attested continental main-phase participation and the applicable first or repeat participation condition. Recorded G025/G026 claims for the same participant and normalized club name and country must be chronologically consistent; a missing country is treated conservatively as a possible match. Older claims remain scored without evidence and need it when corrected; club identity and competition results are not independently verified.
- Administrators can manage a challenge-scoped continental competition catalog with Top, Second, Third or Other tiers. Supported continental claims may optionally reference a competition of an appropriate tier. Distinct linked competitions are distinguished for top-tier stage exclusions; unlinked claims retain conservative same-season exclusions. Catalog tiers and results are self-reported, and domestic, national-team, super-cup and world competitions are not covered. Referenced competitions cannot be deleted or retiered.
- G032 requires a self-attestation of continental titles with two distinct named clubs, including their countries and the first title season. Both clubs can optionally be linked to the club catalog. These claims are not linked to separately recorded competition results.
- New and corrected G037 club-move and G041 country-move claims require the starting club and an eligible new club or country from the player's career. Club milestones combine stints for the same catalog club or normalized club and country; country milestones combine stints across clubs in that country. Complete active per-season match allocations determine the milestone season; otherwise the player attests it on an active stint, with sufficient recorded matches. Later career changes recalculate eligibility. If multiple clubs begin in the earliest recorded season, the starting club is self-attested and must be consistent across these claims. Starting clubs can be linked to the catalog; existing text-only claims remain supported. Legacy claims continue to score until corrected.
- New and corrected G038 and G039 claims require self-attestation of the first first-division experience or first qualifying higher-division club move. G039 also requires the previous best and new division levels; the new level must be higher. Earlier recorded contracts or detailed matches that contradict the claim remove its points; incomplete records and ordering within a season remain uncertain. Older claims retain their points and require evidence when corrected.
- Club stints support audited corrections and removal, with optimistic concurrency on full edits, match updates and deletion. Corrections can change career milestones and scoring; completed challenges must be reopened before editing.
- G005–G007 require the applicable promoted division and confirmation that no corresponding league title was won. G017–G018 require the start season of a two- or three-season consecutive promotion streak with the same stated club. The promotion sequence itself remains self-attested.
- Individual purchases and sales can be recorded per season. The transfer duel uses the maximum of individual fixed-fee sales and the season's quick-summary sale, without summing them. Both managers must use the same currency to compare nonzero sales; a legacy sale in a different currency from its own season also keeps the duel pending. Relabelling a season's transfer currencies requires explicit confirmation and does not convert amounts.
- National team stints are stored separately from club careers and league statistics. Optional per-season match allocations may be recorded for each stint. When they account for every national-team match, G033 is placed automatically in the first season reaching 20 matches; otherwise, the season must be attested on a stint with recorded matches. Earlier manual G033 claims remain supported until a valid milestone season is established; they do not stack with the automatic award. Correcting team records recalculates the milestone, and the career endpoint reports both calculated and attested results. Match records are self-reported.
- A completed challenge can be reopened for corrections only by its administrator. Correction mode keeps the previous final snapshot and prevents an automatic result from being finalised until the administrator explicitly finishes corrections.
- Administrators may archive and later restore a challenge to its previous status. Archiving makes it read-only and revokes pending invitations; restoring does not reactivate those invitations. Correction mode must be finished before archiving.
- For a club with 20 or more official matches across stints, complete season allocations establish the scoring season; otherwise record a supported milestone on an active stint. Until either is available, the milestone remains pending. Later changes can invalidate an attested milestone or change the calculated season.
- Three-, five-, and eight-club milestones (G042–G044) are derived from distinct eligible club records, using the season each club became eligible. Historical manual claims for these milestones remain in the audit history but no longer award points independently.
- The four-country milestone (G047) is derived from distinct country codes and supported country eligibility seasons, including matches accumulated across clubs. Complete active season allocations determine the season; otherwise an attested milestone is used. A historical manual claim cannot award it independently.
- First-division title milestones G045, G046, G049 and G052 are calculated from recorded G001 titles. Club or country details must be present on the title claims for the corresponding distinct-club or distinct-country milestones.
- Existing score projections are rebuilt when a scoring-affecting record changes. An existing completed challenge remains read-only; an administrator must reopen it and finish corrections to rebuild its result with updated scoring logic.
- The database holds the 53 official G001–G053 definitions. New challenges can select which are initially enabled; omitted selections default to all 53 for compatibility. Administrators can enable additional official achievements later through the versioned rules editor.
- `GET /api/geography` returns the database-backed country and continent catalog for authenticated users. The seeded country-to-continent mapping is a fixed geographic classification, including separate Football Manager entries for England, Scotland, Wales and Northern Ireland; see `docs/country-data-license.txt` for the data source and license. Career country codes remain compatible with existing free-text records.
- Authenticated users can search `GET /api/clubs?search=...&countryCode=...` and add a club with `POST /api/clubs`. The shared catalog requires a known country and uniquely identifies entries by normalized name, country, and optional identity label; use a label to add a distinct club with the same name and country. Entries cannot be renamed or removed. Career stints and achievement claims may link a catalog club by `clubId`, while older clients and free-text records remain supported. Linked names and countries must match the catalog; correcting them unlinks the record. Unchanged legacy-client edits preserve the link, and `clearClubId` explicitly removes it. Scoring uses catalog IDs where available and only resolves unlinked text when it identifies a single catalog club; ambiguous text stays unlinked, and a missing country still requires conservative text matching. When several catalog identities share a name and country, career stints must explicitly select an identity; existing text-only stints must be linked before adding another stint for that name and country, preventing an implicit change to historical eligibility.
- G053 uses the country catalog and club or national-team match records. If every stint's matches have season allocations and every stint with matches has a known country, the first season with matches in a third continent is calculated. Otherwise, the player attests the season and the recorded stint totals must support matches in three known continents with an active stint in that season. Correcting records recalculates eligibility; an older manual G053 claim remains a non-stacking fallback. Matches are self-reported.
- The [objective acceptance matrix](docs/objective-acceptance.md) records each official condition and the validations that are still missing. Manual achievement claims are participant attestations and do not yet enforce every objective-specific condition.
- A first-major-club-trophy claim is contradicted by an earlier attested major title or cup with the same identified club. Missing identity, unattested historical results and the order of events within one season remain uncertain; correcting recorded evidence recalculates the score.
- Administrators can create manual custom achievements scoped to one challenge. Both participants may record them subject to their configured frequency. Custom achievements can be created from a selected season, and rule versions preserve existing scheduled changes. Completed challenges must be reopened before rules can change.
- Per-club and club-season claim frequencies distinguish normalized club names in different countries and use catalog identities where available. When multiple catalog identities share a name and country, ambiguous text claims remain unlinked and conservatively match a possible linked identity for scoring. Explicitly link the older claim before recording a distinct club with the same name and country. A missing country may match any country with that name to avoid duplicate awards. Unlinked names and country codes remain user-entered; correcting them can change the score.
- Provider credentials are intentionally unset. The API returns a clear 503 response when a provider is not configured.

## Independent repository setup

This folder is the backend project root and can be initialized and published independently of `frontend/`. It does not depend on a root solution file or shared package manifest.
