# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

`MinitApi` is the backend for **Minit**, a WhatsApp-like voice-call + messaging app where users register by `DisplayName` only (no phone/email) and are identified by a server-generated unique 8-character `Code`. The backend enforces a per-user **monthly call-second quota** and relays WebRTC signaling.

The Android client lives in a sibling repo at `../MinitAppAndroid` (Kotlin / Jetpack Compose / Hilt / WebRTC / FCM) and is a second working directory in this workspace. It talks to this backend over REST (`/api/...`), the WebSocket signaling channel (`/ws`), and receives incoming-call/message pushes via FCM.

> Naming note: the solution and project file are `workspace.sln` / `workspace.csproj` and the root namespace is `Workspace.*`, even though the repo/app is "Minit". The test project is `Workspace.Api.Tests`.

## Commands

All commands run from the repo root (`d:\MyProjects\Minit\MinitApi`).

```powershell
dotnet build workspace.sln                 # build API + tests
dotnet run                                 # run the API (Development); Swagger at /swagger
dotnet test                                # run all integration tests
```

Run a single test:

```powershell
dotnet test --filter "FullyQualifiedName~ApiIntegrationTests.<MethodName>"
```

EF Core migrations (Npgsql / PostgreSQL, code-first):

```powershell
dotnet ef migrations add <Name>            # add a migration
dotnet ef database update                  # apply locally
```

The app **auto-applies migrations on startup** (`db.Database.Migrate()` in `Program.cs`) for every non-`Testing` environment — there is no manual deploy step.

## Architecture

Single ASP.NET Core **Minimal API** project (.NET 10, C# 14, nullable + implicit usings enabled). No MVC controllers. Wiring lives entirely in `Program.cs`; the layout is feature-folder, not layered-by-tech.

- **`Endpoints/`** — one static class per feature exposing a `MapXEndpoints(this RouteGroupBuilder)` extension; all are mounted under `/api` in `Program.cs`. Handlers are `private static async Task<IResult>` methods that take dependencies (`AppDbContext`, services) as parameters via DI. This is where request logic lives — there is no separate controller/service split for most features.
- **`Services/`** — cross-cutting logic injected into handlers: `QuotaService` (billing math), `PushService` (FCM), `InviteService` (one-time invite codes), `TurnService` (TURN/STUN credentials via `HttpClient`), `CodeGenerator`. Plus two `IHostedService` background workers: `CallTimeoutService` (auto-ends stale calls) and `CallHistoryCleanupWorker` (honors per-user auto-delete retention).
- **`Domain/`** — EF entities + enums. **No data annotations** — all schema config (table names, indexes, FKs, enum conversions, `timestamp with time zone` columns) is fluent in `Data/AppDbContext.cs::OnModelCreating`. When adding/changing a field, edit it there and generate a migration.
- **`Dtos/`** — request/response records grouped by feature; the wire contract for the Android client.
- **`WebSockets/`** — WebRTC signaling (see below).
- **`Security/AdminApiKeyAuth.cs`** — `X-Admin-Key` header check.

### Conventions to match

- **Errors** use RFC7807 `ProblemDetails` via `Results.Problem(...)` with a stable machine-readable `code` in `extensions` (e.g. `"quota_exceeded"`, `"call_not_found"`). Input validation uses `Results.ValidationProblem(...)`. The Android client keys off these. Match this pattern in new handlers rather than throwing or returning bare status codes.
- **Identity is passed explicitly** — there is no auth middleware/principal. Endpoints receive `userId`/`createdByUserId` etc. in the request body or the `X-User-Id` header (used for rate-limit partitioning). Admin endpoints are the only ones guarded (`X-Admin-Key`).
- **Time is always UTC** (`DateTime.UtcNow`), stored as `timestamp with time zone`. Months are encoded as an `int` `YYYYMM` via `QuotaService.GetMonthUtc`.
- **Multi-row billing/state changes run in an explicit EF transaction** with manual rollback (see `EndCallAsync`). `MonthlyUsage` rows are created race-safely with a raw `INSERT ... ON CONFLICT DO NOTHING` in `QuotaService.GetOrCreateMonthlyUsage`.
- **Side effects that shouldn't block the response** (e.g. FCM push on call start) are fire-and-forget on a **new DI scope** via `IServiceScopeFactory` with `CancellationToken.None` — the request scope's `AppDbContext` is gone by then.

### Call + signaling flow

1. `POST /api/calls/start` — validates both users active, checks quota (`CheckRemainingSeconds`), creates `CallSession` (`Active`) + creator `CallParticipant`, fires an FCM incoming-call push to the callee.
2. Both peers open a WebSocket to `/ws?callId=<guid>&userId=<guid>`. `SignalingRoomManager` keeps in-memory rooms; `SignalingHandler` relays every message (SDP offers/answers, ICE) to the other peers and emits `peer_joined` / `peer_left` control frames. The new peer is told about already-present peers to avoid an offer/answer race when FCM ordering is unpredictable. **Signaling state is in-memory only** — it does not survive a restart and does not scale across instances without sticky routing.
3. `POST /api/calls/{id}/join` records the callee participant.
4. `POST /api/calls/{id}/end` — in a transaction: marks `Ended`, computes `billableSeconds` from `StartedAt`, calls `QuotaService.ApplyBilling` (increments the creator's `MonthlyUsage`). Only the **call creator** is billed. Ending an already-ended call is idempotent.

## Configuration

Config keys (env vars override `appsettings.json`; Railway-style deployment):

- `ConnectionStrings:DefaultConnection` or env `DATABASE_URL` — `Program.cs` parses `postgresql://`/`postgres://` URIs into a key=value Npgsql string.
- `PORT` (env) — bound on `0.0.0.0`; defaults to 8080.
- `Admin:ApiKey` — value expected in the `X-Admin-Key` header.
- `Security:InviteCodePepper` — **required**; `InviteService` throws on startup use if unset. Invite tokens are stored only as a salted/peppered hash.
- `FIREBASE_SERVICE_ACCOUNT_JSON` (env) — Firebase Admin credential JSON for FCM. If unset, pushes are skipped (logged, not fatal).

Rate limiting (built-in `AddRateLimiter`): `public-per-ip` (30/min by IP) and `invites-per-user-or-ip` (10/min, partitioned by `X-User-Id` then IP). Rejections return 429 as `ProblemDetails`.

> `appsettings.json` currently contains a real-looking DB connection string and placeholder secrets. Treat these as environment-provided in any real deployment; do not rely on the checked-in values.

## Testing

`tests/Workspace.Api.Tests` uses xUnit + `WebApplicationFactory<Program>`. `ApiTestFactory` runs the app under the **`Testing`** environment, which (in `Program.cs`) skips Npgsql registration and migration; the factory instead swaps in **EF Core SQLite in-memory** (`:memory:`, shared connection) and injects test config (`Admin:ApiKey`, `Security:InviteCodePepper`). Tests exercise real HTTP endpoints end-to-end. `Program.cs` ends with `public partial class Program;` specifically so the factory can reference it.
