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

### Docker Compose

```bash
cd building-precheck/infra
docker compose up --build
```

Docker Compose starts PostGIS, imports the official local snapshots, runs the
API, and serves the React SPA. The first run imports the national cadastral
geometry and can take several minutes. Later runs repeat the import so a change
in a source snapshot is always reflected in the database.

After it finishes:

- SPA: `http://localhost:8081`
- API: `http://localhost:8080`
- OpenAPI: `http://localhost:8080/openapi/v1.json`

### Aspire

The AppHost project is retained as an Aspire-ready service-layout reference.
For this data-heavy MVP, Docker Compose is the supported local launcher because
it provides the GDAL container used to translate Shapefile and GeoPackage data.

Manual fallback:

```bash
docker run --platform linux/amd64 --name vai-drikstu-postgis -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=vaidrikstu -p 5432:5432 postgis/postgis:17-3.5
docker compose -f infra/docker-compose.yml run --rm geo-importer
dotnet run --project src/VaiDrikstu.DataImporter
dotnet run --project src/VaiDrikstu.Api
pnpm --dir web dev
```

## Real-data checks

The result depends on the selected action and actual spatial findings. A checked
address can be green when no selected-action restriction is found in the loaded
open data, or yellow where a heritage/nature finding requires an official review.

## Rule table

`src/VaiDrikstu.Domain/PrecheckRuleEngine.cs` contains `Rules`: one row per
action × restriction with the likely procedure code (`NKMP_COORDINATION_LIKELY`,
`DAP_CONDITIONS_LIKELY`, `MUNICIPAL_APPROVAL_LIKELY`) and the next step shown to
the user. Every result also lists what was not checked (territorial planning,
municipal binding regulations, encumbrances, co-owner consent).

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

Docker Compose loads the official local snapshots in `data/sources` rather than
the old demo JSON fixtures:

- VZD State Address Register: active, confirmed address points nationwide;
- VZD cadastral parcel and parcel-part geometry plus regional encumbrance CSVs;
- NKMP state-protected immovable-monument register, joined to cadastral geometry
  only when its cadastral designation is valid and present in the source data;
- DAP/ĢeoLatvija protected nature-area polygons;
- Rīga territory-plan functional zoning and encumbrance polygons;
- Rīga historical-centre and protection-zone planning polygons.
- Planning-region to administrative-territory mappings.

The importer preserves TAPIS and flood-risk WMS metadata in `source_catalog`.
They are marked `reference-only`: the supplied TAPIS extract is partial and the
flood files are WMS capability documents, not vector features suitable for a
deterministic PostGIS intersection.

The old `*.demo.json` files are retained only as historical fixtures and are no
longer read by Docker Compose or the data importer.
