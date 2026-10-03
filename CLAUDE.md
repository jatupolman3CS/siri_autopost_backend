# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

FB Group AutoPost: a Chrome extension that posts to Facebook groups while imitating a human, plus an optional .NET server that lets people edit configs and control the extension remotely. The repo holds two parts that depend on each other:

- `client/`: the Chrome extension (Manifest V3, plain ES modules, no build step).
- `backend/AutoPost.Server/`: an ASP.NET Core (.NET 10) minimal API with EF Core and PostgreSQL.

User-facing strings (UI text, API error messages, log messages) are in **Thai**. Code comments are in English. Keep both conventions.

The sibling repo `siri_autopost_ui` is a Vite-hosted copy of the web UI. See "Duplicated files" below.

## Commands

```bash
# Backend: local dev (http://localhost:5080, ASPNETCORE_ENVIRONMENT=Development)
cd backend/AutoPost.Server && dotnet run
dotnet build backend/AutoPost.Server/AutoPost.Server.csproj

# Backend + Postgres in Docker (run from the repo root; needs .env with DB_PASSWORD and ADMIN_PASSWORD)
cp .env.example .env && docker compose up -d --build     # serves on ${PORT:-8080}

# EF Core migrations (migrations run automatically on startup)
cd backend/AutoPost.Server && dotnet ef migrations add <Name> --output-dir Data/Migrations

# Extension: load client/ unpacked at chrome://extensions (Developer mode)

# End-to-end test: runs the real background.js (chrome.* mocked) against a running server
node client/tools/test-online.mjs http://localhost:5080 admin <admin-password>

# Turn a SIRI_autopost_export.zip into client/config/autopost-config.json (options in client/README.md)
node client/tools/siri-to-config.mjs <export.zip> [options]
```

There are no unit test projects and no linter config. `test-online.mjs` is the only automated test, and it needs a live server and database.

## Backend configuration quirks

- `Program.cs` reads a `.env` file from the current directory or its parent before startup. Variables already set in the environment win.
- Connection string: `ConnectionStrings:Default`, falling back to `AppSettings:ConnectionStrings`. Its database name is then **replaced** by `Database:Name`: `SIRIAUTOPOST` in Development and `SIRIAUTOPOST_PRD` otherwise, Docker included. The server creates the database on first start, so the DB user needs CREATEDB.
- The database is shared with other apps. Every table is prefixed `fbap_`, the migrations history table is `fbap_ef_migrations`, and columns are snake_case (`UseSnakeCaseNamingConvention`).
- The first start seeds an admin from `Admin:Username`/`Admin:Password`. If no password is set, it generates one and logs it.
- Use the **root** `docker-compose.yml`. `backend/docker-compose.yml` still points at the old `server/Dockerfile` layout and is broken. `client/README.md` and `client/.dockerignore` also still refer to `server/`.
- The `<None Include="..\..\dashboard.html" ...>` items in the csproj resolve to the repo root, not `client/`, so a plain `dotnet publish` doesn't copy the extension files. The Dockerfile copies them into `/out/extension` itself.

## Architecture

### Three API surfaces (`backend/AutoPost.Server/Endpoints/`)

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

`siri_autopost_ui` contains byte-identical copies of `client/dashboard.{css,js}`, `client/lib/*` and `client/icons/`, and near-copies of `client/dashboard.html` and `backend/AutoPost.Server/wwwroot/{index,login}.html` + `web/*`. The near-copies drop the `/app/` path prefix and load the shim as a module. When you change any of these files, mirror the change in the other repo.
