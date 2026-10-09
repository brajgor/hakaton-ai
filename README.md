# Vai drīkstu te darīt?

Hackathon MVP for a VDAA-compatible Latvija.gov.lv style e-service. The service
performs a deterministic open-data pre-check for a selected action and address.
It is not a legal permit issuer.

## Stack

- .NET 10 ASP.NET Core Minimal API BFF
- React + TypeScript + Vite SPA
- PostGIS for spatial checks
- .NET Aspire AppHost for local orchestration, plus Docker Compose as a portable fallback
- Deterministic rules engine, no LLM in the decision path

## Run locally

### Aspire

```bash
cd vdaa-vai-drikstu
dotnet restore
pnpm --dir web install
dotnet run --project src/VaiDrikstu.AppHost
```

The AppHost starts PostGIS, imports the demo data, runs the API, and starts the
Vite frontend. The PostGIS image is pinned to `postgis/postgis:17-3.5` and the
AppHost passes `--platform linux/amd64` for Apple Silicon compatibility.

### Docker Compose

```bash
cd vdaa-vai-drikstu/infra
docker compose up --build
```

Docker Compose starts the PostGIS container, imports demo data, runs the API,
and serves the React SPA.

Manual fallback:

```bash
docker run --platform linux/amd64 --name vai-drikstu-postgis -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=vaidrikstu -p 5432:5432 postgis/postgis:17-3.5
dotnet run --project src/VaiDrikstu.DataImporter
dotnet run --project src/VaiDrikstu.Api
pnpm --dir web dev
```

## Demo searches

- `Zvaigžņu` -> green case
- `Pils laukums` -> cultural heritage yellow case
- `Meža prospekts` -> protected nature yellow case

## API

- `GET /api/session/mock`
- `GET /api/intents`
- `GET /api/addresses/search?q=...`
- `POST /api/precheck`
- `GET /api/precheck/{requestId}/summary`
- `GET /openapi/v1.json`

## VDAA alignment

The MVP follows the local `vdaa-epakalpojumi` skill constraints:

- React SPA path with a BFF/API layer.
- REST JSON and OpenAPI.
- Server-side validation and deterministic reason codes.
- No direct frontend registry access.
- 4-step mobile-first flow with a summary step.
- Mock Latvija.lv session and milestones only; real OIDC/PFAS/VISS/AMK are
  documented production integration points.

## Data scope

`data/sources` contains tiny curated demo snapshots. Production ingestion should
replace them with official VZD address data, NKMP cultural heritage data, and
DAP/ĢeoLatvija protected nature data.
