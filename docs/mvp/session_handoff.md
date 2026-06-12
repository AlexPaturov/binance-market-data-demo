# Session Handoff

## Rules

- After changes, provide commit message text only.
- Commit format: Conventional Commits.
- Allowed types:
  - feat
  - fix
  - breaking
  - chore
  - docs
  - refactor
  - test
  - build
  - ci
  - style
  - perf
- Ask for approval only when needed for elevated/destructive/out-of-sandbox actions.

## Project MVP Direction

Accepted MVP execution order:

1. Authorization.
2. Tests.
3. Pipeline.
4. Remaining MVP cleanup/docs.

Auth decision:

- Keep Azure B2C / OIDC.
- Do not switch to local-only auth.
- Do not build full IAM / permissions / scopes system for MVP.
- Minimal roles later:
  - Viewer
  - Operator
  - Admin

## Current Git/Files Context

Tracked code changed in this session:

- `src/BinanceDataCollector.DataManager/Program.cs`
- `src/BinanceDataCollector.DataManager/Common/AllowAllConnectionsFilter.cs`
- `docs/mvp/session_handoff.md`

Ignored local context:

- `src/BinanceDataCollector.DataManager/Properties/launchSettings.json` is local/ignored and contains local dev settings, including B2C secret.
- `launchSettings.json` was removed from Git tracking and pushed in commit `b8e7d46`.

MVP docs currently in `docs/mvp`:

- `project_resume.md`
- `auth_mvp_plan.md`
- `mvp_execution_order.md`
- `auth_decision.md`
- `session_handoff.md`

## Code Changes Already Made

In `src/BinanceDataCollector.DataManager/Program.cs`:

- Added `using Microsoft.AspNetCore.Authorization;`
- Changed `builder.Services.AddAuthorization();` to fallback policy requiring authenticated user.
- Enabled middleware:
  - `app.UseCookiePolicy();`
  - `app.UseAuthentication();`
  - `app.UseAuthorization();`
- Left health endpoints anonymous:
  - `/health/live`
  - `/health/ready`

Build check passed:

```bash
dotnet build src/BinanceDataCollector.DataManager/BinanceDataCollector.DataManager.csproj --no-restore
```

Result: success.

Suggested commit message for the tracked code change:

```text
fix: enable Azure B2C authentication for DataManager
```

## Local Dev Auth Issue

After enabling auth, Azure B2C callback failed with:

```text
AADB2C90079: Clients must send a client_secret when redeeming a confidential grant.
```

Cause:

- B2C app is confidential client.
- `Authentication:B2C:ClientSecret` is null in local config.

Prod path:

```text
GitHub Actions secrets -> server /opt/BinanceCollector/docker/compose/.env -> docker-compose.prod.yml -> container env -> ASP.NET config
```

Relevant prod compose keys:

```yaml
Authentication__B2C__ClientId=${AUTH_B2C_CLIENTID}
Authentication__B2C__ClientSecret=${AUTH_B2C_CLIENT_SECRET}
```

Dev path:

- Worker/DataManager are run locally from Rider/dotnet, not in Docker.
- Docker is only infrastructure.
- Dev variables are loaded from:
  - `launchSettings.json`
  - Rider run configuration
  - user-secrets
  - process env

Docs confirming this:

- `docs/dev/ARCHITECTURE_DEV.md`
- `docs/dev/MIGRATE_TO_LINUX.md`

`dotnet user-secrets list --project src/BinanceDataCollector.DataManager/BinanceDataCollector.DataManager.csproj`

returned:

```text
No secrets configured for this application.
```

## Local launchSettings

Local file:

```text
src/BinanceDataCollector.DataManager/Properties/launchSettings.json
```

It already had:

- connection strings
- RabbitMQ env
- Seq env

It was missing B2C secret.

Added locally:

```json
"Authentication__B2C__ClientId": "83d0093c-4bc3-4484-add5-3c089620ce76",
"Authentication__B2C__ClientSecret": "PUT_B2C_CLIENT_SECRET_HERE"
```

Real value was placed locally in ignored `launchSettings.json`; do not commit the actual secret.

To get prod `.env` on server, user demanded full file output. Final command given:

```bash
cd /opt/BinanceCollector/docker/compose
sudo cat .env
```

Safer command for only B2C secret, if needed:

```bash
cd /opt/BinanceCollector/docker/compose
sudo sed -n 's/^AUTH_B2C_CLIENT_SECRET=//p' .env
```

## Current Session Update - 2026-06-12

Auth and access:

- Local Tailscale is configured and `analserver` is reachable again.
- SSH key auth for `ssh prod` works.
- Prod B2C secret was read from server `.env` and placed in local `launchSettings.json`.
- `.NET user-secrets` for DataManager is empty by user preference.
- B2C login was verified successfully by the user.

Git/config:

- `src/BinanceDataCollector.DataManager/Properties/launchSettings.json` was removed from Git tracking and remains ignored by `.gitignore`.
- Commit already pushed: `b8e7d46 chore: stop tracking DataManager launch settings`.

Hangfire auth:

- DataManager Hangfire dashboard returned 403 on statistics refresh after B2C login.
- Local fix made:
  - `MapHangfireDashboard` changed to `UseHangfireDashboard`.
  - Dashboard auth filter now allows authenticated users only.
- Build passed after this fix:

```bash
dotnet build src/BinanceDataCollector.DataManager/BinanceDataCollector.DataManager.csproj --no-restore
```

Archive import/data quality:

- Archive CSV import is running for partially loaded historical data.
- Database growth is expected to appear across `Trades_YYYY_MM` partitions, not only parent `Trades`.
- Current `DataQualityReports` should be treated as draft/partial because imports are incomplete.
- Safe pause/resume guidance: stop Worker only, keep Postgres running, do not delete Hangfire jobs if resuming later. Bulk inserts are idempotent via `ON CONFLICT ... DO NOTHING`; resuming may re-read part of a CSV but should not duplicate trades.

## Next Steps

1. Let archive import finish, or pause it by gracefully stopping only Worker.
2. After import completion, verify coverage by symbol/month in `Trades` partitions.
3. Clear or ignore draft `DataQualityReports` from partial import runs.
4. Run `DataQualityWorker.CheckMonthAsync(year, month)` for confirmed complete months.
5. Verify the Hangfire 403 fix in browser after restarting DataManager.
6. Replace authenticated-only Hangfire access with Admin-only filter.
7. Continue MVP order: tests, pipeline, remaining cleanup/docs.
