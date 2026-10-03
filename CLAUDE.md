# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

FB Group AutoPost: a Chrome extension that posts to Facebook groups while imitating a human, plus .NET servers that let people edit configs and control the extension remotely. The repo holds three parts:

- `src/` + `tests/` + `SIRIAUTOPOST.sln`: the **new** Clean Architecture solution (.NET 10, EF Core, PostgreSQL). It is the API behind the Angular dashboard (JWT auth, workspaces, social accounts, post scheduling, error reports, media library, anti-ban/offline settings). It is not wired to the extension yet, so nothing actually posts. New backend work goes here.
- `backend/SIRI.AUTOPOST.Server/`: the **legacy** server, a single-project minimal API that the extension and the legacy web UI talk to today.
- `client/`: the Chrome extension (Manifest V3, plain ES modules, no build step).

User-facing strings (UI text, API error messages, log messages) are in **Thai**. Code comments are in English. Keep both conventions.

The sibling repo `siri_autopost_ui` holds the new Angular frontend (the AutoPost Dashboard design, talking to `SIRIAUTOPOST.Api`) at its root, and a Vite-hosted copy of the legacy web UI in `legacy/`. See "Duplicated files" below.

## New solution (`SIRIAUTOPOST.sln`)

```bash
dotnet build SIRIAUTOPOST.sln
dotnet test SIRIAUTOPOST.sln                     # integration tests need PostgreSQL, see below
dotnet test tests/SIRIAUTOPOST.Domain.Tests      # one project
dotnet test SIRIAUTOPOST.sln --filter "FullyQualifiedName~PostTests"   # one class/test

# API on http://localhost:5100 (OpenAPI at /openapi/v1.json in Development)
cd src/SIRIAUTOPOST.Api && dotnet run

# Migrations live in Infrastructure; the Api is the startup project
dotnet ef migrations add <Name> -p src/SIRIAUTOPOST.Infrastructure -s src/SIRIAUTOPOST.Api -o Data/Migrations
```

- Dev connection string is in `src/SIRIAUTOPOST.Api/appsettings.Development.json` (`localhost:5432`, database `siriautopost`). `Database:MigrateOnStartup` is true only in Development.
- Auth is JWT bearer (`Infrastructure/Auth`, `Jwt` config section). `Jwt:Key` must be at least 32 characters or startup fails; only Development ships one. A fallback policy requires a signed-in user everywhere, so anonymous endpoints need `[AllowAnonymous]`. Handlers get the caller from `ICurrentUser` (the `sub` claim) and check workspace ownership with `WorkspaceAccess.RequireOwnedAsync`, which answers 404 for someone else's workspace.
- Startup (`PrepareDatabaseAsync`) migrates when configured, then `AdminAccountSeeder` creates the platform admin from `Admin:Email`/`Admin:Password` (Development: `admin@autopost.local` / `admin1234`).
- Every new workspace is filled by `DemoWorkspaceSeeder` (`IWorkspaceSeeder`): the design's 7 social accounts (the Facebook page has 20 groups), 4 snippets, a week of post history, a week of queued posts and 5 open error reports. Accounts cannot be connected through the extension yet, and no worker posts anything, so queued posts stay queued. Integration tests must not assume an empty workspace.
- `SIRIAUTOPOST.Api.IntegrationTests` runs the real API through `WebApplicationFactory<Program>` against PostgreSQL. It drops and recreates the database `siriautopost_test` (override the whole connection string with `SIRIAUTOPOST_TEST_DB`).
- Layer rules. **Domain** references nothing. **Application** references Domain only. **Infrastructure** references Application and Domain. **Api** references Application and Infrastructure. Keep entity invariants in Domain (`Post.Schedule`/`Retry`/`DismissError`, `Workspace.UpdateAntiBan`, `AntiBanSettings.Validate`, `MediaFile.Create`) and input-shape checks in FluentValidation validators (`Application/Validators/Validators.cs`).
- CQRS without a mediator library. Each command or query is a record plus a handler, grouped per feature in `Application/Features/<Feature>/<Feature>.cs`, and it has to be registered in `Application/DependencyInjection.cs`. `AddCommand<,,>` wraps each handler in `ValidationCommandHandlerDecorator`, so validators always run first. Controllers inject `ICommandHandler<,>`/`IQueryHandler<,>` with `[FromServices]`.
- Errors. `ExceptionHandlingMiddleware` maps `ValidationException` to 400 (`ValidationProblemDetails` with camelCase keys), `AuthenticationException` to 401, `NotFoundException` to 404, `ConflictException` to 409 and `DomainException` to 422. Anything else becomes a 500.
- JSON: enums are snake_case strings (`fb`, `pending_approval`) and numbers are strict JSON numbers, set for both MVC and `ConfigureHttpJsonOptions` so the OpenAPI document (and the UI's generated types) match. The client sends `startAt` with its local UTC offset so weekday repeats follow the user's calendar.
- EF Core: one `IEntityTypeConfiguration` per entity in `Infrastructure/Data/Configurations/Configurations.cs`, snake_case naming, enums stored as strings, and `AppDbContext` doubles as `IUnitOfWork`. Workspace anti-ban/offline settings are owned JSON (`jsonb`) columns. Store `DateTimeOffset` values as UTC, because Npgsql rejects non-zero offsets for `timestamptz`.
- When the API contract changes, refresh `openapi.snapshot.json` in `siri_autopost_ui` (`curl localhost:5100/openapi/v1.json`) and run `npm run gen:api` there.

## Legacy server and extension commands

```bash
# Backend: local dev (http://localhost:5080, ASPNETCORE_ENVIRONMENT=Development)
cd backend/SIRI.AUTOPOST.Server && dotnet run
dotnet build backend/SIRI.AUTOPOST.Server/SIRI.AUTOPOST.Server.csproj

# Backend + Postgres in Docker (run from the repo root; needs .env with DB_PASSWORD and ADMIN_PASSWORD)
cp .env.example .env && docker compose up -d --build     # serves on ${PORT:-8080}

# EF Core migrations (migrations run automatically on startup)
cd backend/SIRI.AUTOPOST.Server && dotnet ef migrations add <Name> --output-dir Data/Migrations

# Extension: load client/ unpacked at chrome://extensions (Developer mode)

# End-to-end test: runs the real background.js (chrome.* mocked) against a running server
node client/tools/test-online.mjs http://localhost:5080 admin <admin-password>

# Turn a SIRI_autopost_export.zip into client/config/autopost-config.json (options in client/README.md)
node client/tools/siri-to-config.mjs <export.zip> [options]
```

The legacy server has no unit test project. Its only automated test is `test-online.mjs`, which needs a live server and database. Neither part has a linter config.

## Legacy backend configuration quirks

- `Program.cs` reads a `.env` file from the current directory or its parent before startup. Variables already set in the environment win.
- Connection string: `ConnectionStrings:Default`, falling back to `AppSettings:ConnectionStrings`. Its database name is then **replaced** by `Database:Name`: `SIRIAUTOPOST` in Development and `SIRIAUTOPOST_PRD` otherwise, Docker included. The server creates the database on first start, so the DB user needs CREATEDB.
- The database is shared with other apps. Every table is prefixed `fbap_`, the migrations history table is `fbap_ef_migrations`, and columns are snake_case (`UseSnakeCaseNamingConvention`).
- The first start seeds an admin from `Admin:Username`/`Admin:Password`. If no password is set, it generates one and logs it.
- Use the **root** `docker-compose.yml`. `backend/docker-compose.yml` still points at the old `server/Dockerfile` layout and is broken. `client/README.md` and `client/.dockerignore` also still refer to `server/`.
- The `<None Include="..\..\dashboard.html" ...>` items in the csproj resolve to the repo root, not `client/`, so a plain `dotnet publish` doesn't copy the extension files. The Dockerfile copies them into `/out/extension` itself.

## Legacy architecture

### Three API surfaces (`backend/SIRI.AUTOPOST.Server/Endpoints/`)

- `AuthEndpoints`: `/api/auth/*`, cookie auth (`fbap.auth`). Unauthenticated `/api` calls get a 401; page requests are redirected to `/login.html`.
- `AdminEndpoints`: `/api/*` (requires login). Covers profiles (configs), profile images, devices, live device state and logs, and remote commands.
- `DeviceEndpoints`: `/api/device/*`, used by the extension's service worker. It authenticates with the `X-Device-Key` header (only its SHA-256 hash is stored) and has an open CORS policy. `POST /heartbeat` uploads state and logs, and returns the config revision and any pending commands.

### Domain model (`Data/AppDb.cs`)

- **Profile**: one extension "settings" JSON blob (version 2 shape: `campaigns`, groups, posts, global settings, Telegram), stored as `jsonb`, with a `Revision` counter. Several devices can share one profile.
- **ProfileImage**: media stored as bytes, keyed `(ProfileId, ImageId)`. The ID matches the extension's `img:<id>` storage key. An image ID never changes content, so existing IDs are never overwritten.
- **Device**: one computer running the extension, with its last reported `State` (jsonb). It counts as online if seen within the last 100s (`AdminEndpoints.OnlineWindow`).
- **DeviceCommand**: a queued remote command (pending → sent → done/expired). Commands older than 10 minutes expire instead of running late.

### Settings sync (`Services/ProfileStore.cs` + `client/background.js` "online" section)

- `SaveSettings` uses optimistic concurrency. The caller sends `baseRevision`; if the row has moved on, the API returns 409, and `null` overwrites. Saving identical JSON keeps the revision so devices don't reload for nothing.
- After each save, images that no settings reference and that are older than 1 hour are deleted. The grace period exists because editors upload images before saving the settings that use them.
- The extension syncs every 30s through a `chrome.alarms` entry (`fbap-sync`). It pulls when the revision changes, fetches only missing images, and pushes local edits after about 4s. When both sides changed, the server wins.
- `Services/Helpers.cs` `SettingsJson` reads the extension's JSON on the server side. It mirrors `migrateSettings`/`campaignImageIds` in `client/lib/shared.js` and `hasContent` in `client/lib/backup.js`. If the settings shape changes, update both sides.

### The web editor reuses the extension's dashboard

The server serves `client/dashboard.{html,css,js}`, `lib/*.js` and `icons/` under `/app/`, located at runtime by `ExtensionPath()`. `GET /app/dashboard.html` injects `wwwroot/web/shim.js` before `dashboard.js`. The shim replaces `chrome.storage.local`, `chrome.runtime.sendMessage` and `chrome.permissions` with REST calls:

- `settings` and `img:*` storage keys map to profile endpoints (`?profile=<id>`).
- `sendMessage` commands become `DeviceCommand`s for the selected device (`?device=<id>`). The shim polls until the command is done.

So `dashboard.js` must keep talking only through those chrome APIs. A new chrome API used there needs a shim implementation too. `wwwroot/index.html` + `web/admin.js` hold the profile and device management page.

### Extension internals (`client/`)

- `background.js` (service worker): every campaign has its own `chrome.alarms` entry (`fbap:<campaignId>`). Ticks are serialized through one promise chain, so only one post happens at a time across all campaigns, in a dedicated worker window. It also handles anti-block rules (daily quotas, group cooldowns, auto-pause on Facebook warnings), Telegram notifications, the bundled-config loader, and server sync.
- `content.js` is injected with `chrome.scripting.executeScript` into the Facebook tab. It does the DOM steps (scroll, type, attach, click post).
- Messaging is `{ target: 'fbap-bg' | 'fbap-content', cmd, ...args }` → `{ ok, ... }`. The dashboard calls background commands, and background calls content handlers.
- Remote commands: the names in `AdminEndpoints.AllowedCommands` must match `remoteCommands` in `background.js`, and the shim's `LOCAL_ONLY` list blocks commands the web can't run.
- `lib/shared.js` holds settings defaults, `migrateSettings`, spintax and the text composer. `lib/backup.js` handles import/export and the bundled `config/autopost-config.json`, which loads automatically only when the browser profile has no data. `lib/siri-import.js` + `lib/zip.js` import SIRI exports.
- The files under `client/config/` (`extra-groups.txt`, `drop-images.txt`, `image-fixes/`) are only inputs for `tools/siri-to-config.mjs`. The extension never reads them at runtime.

## Duplicated files

`siri_autopost_ui/legacy/` contains byte-identical copies of `client/dashboard.{css,js}`, `client/lib/*` and `client/icons/`, and near-copies of `client/dashboard.html` and `backend/SIRI.AUTOPOST.Server/wwwroot/{index,login}.html` + `web/*`. The near-copies drop the `/app/` path prefix and load the shim as a module. When you change any of these files, mirror the change in the other repo.
