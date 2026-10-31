# AI Lead Generation System

Built from SRS v1.0 (30 Oct 2026). Finds people in Egypt and the Gulf who have posted an active request for a
software project, scores them, and shows them in an inbox. **All outreach is manual.**

## Layout
- `backend/LeadGen.Core/`: all pipeline logic with no NuGet packages: connector (X), AI client (Google Gemini), scoring, pipeline, scheduling (claim, retry, next-run calculation).
- `backend/LeadGen.Core.Tests/`: test harness (runs offline against an in-process fake AI; no Python service required).
- `backend/LeadGen.Api/`: ASP.NET Core 8 adapter: JWT auth, EF Core/PostgreSQL stores, endpoints, scheduler worker.
- `frontend/`: React + TypeScript. Lead Inbox (with "needs review"), Lead Details, Search setup (services, phrases, schedule, exclusion rules), Data sources, Reports.
- `db/init.sql` (fresh install) and `db/migrations/` (001 exclusions, 002 schedule for existing databases).

## Run
```bash
cp .env.example .env      # set passwords, X_BEARER_TOKEN and GEMINI_API_KEY
docker compose up --build
# dashboard: http://localhost:3000   API: http://localhost:8080
```
Then, in the dashboard: sign in, open **Data sources** and turn X on, open **Search setup**, pick your services, add
suggested phrases, and press **Save and run search**. Leads appear in the **Lead Inbox**.

## Tests
```bash
./scripts/run_core_tests.sh all   # 161 pipeline checks: stage1..stage4 (offline, no AI service to start)
```
`LeadGen.Core` and its tests restore with an offline `nuget.config` (no packages needed).

## Status
| Part | State |
|---|---|
| AI client | Google Gemini (gemini-2.5-flash-lite) via REST, credentials in .env |
| LeadGen.Core (pipeline, scheduler, X connector) | compiles; checks pass |
| Frontend | type-checks and builds |
| LeadGen.Api (EF Core, endpoints, worker) | written, **never compiled**: it needs NuGet packages that were unavailable in the build sandbox. Run `dotnet build backend/LeadGen.Api` first and fix anything it reports |
| Docker Compose / PostgreSQL | written, not run end to end |

## Not built yet (from the SRS)
Dashboard home page; CSV/XLSX export; high-priority notification UI; BGE-M3 semantic dedupe; retention jobs.

## Rules to keep
Use official APIs only (X needs a paid tier with search access). No scraping, no automated messages or DMs.
