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
API, and serves the React SPA. By default it loads a tiny, versioned demo
dataset with three scenarios: green, heritage review, and nature review. It is
intended for the hackathon presentation, not for legal or spatial conclusions.

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

### Full official-data import and Rīga rule layers (local only)

Official snapshots are deliberately excluded from Git. Put them under
`.local-data/sources/`; place the separately collected regulation and Rīga
snapshots in `new_data/`; then run:

```bash
cd building-precheck/infra
docker compose -f docker-compose.full.yml up --build
```

This imports nationwide VZD, DAP and NKMP data, plus Rīga functional zoning,
red lines and selected RTP 2030 protection-zone layers. It can take several
minutes and needs roughly 2 GB of local disk data. Both local source folders
are Git-ignored and mounted read-only; they never enter a Docker build context
or GitHub.

## Real-data checks and rule registry

`src/VaiDrikstu.Domain/RuleCatalog.cs` is the reviewed, versioned MVP catalog.
It is copied on API start into the `legal_sources`, `legal_source_versions`,
`rules`, `rule_conditions`, `rule_effects` and `rule_citations` tables. Each
pre-check saves the project input, checking date, geographic findings, applied
rule versions, citations and the complete result to `precheck_runs`.

The engine never asks an LLM to interpret a law. It applies only active rules
for the address municipality, selected action and loaded spatial flags. The API
returns both a presentation level and an auditable status:

| API level | Regulatory status | Meaning |
| --- | --- | --- |
| `green` | `no_known_blocker` | No selected-action restriction was found in the loaded data. It is not a permit. |
| `yellow` | `conditions_apply` | A protected-area or flood-risk condition needs an authority check. |
| `yellow` | `manual_review_required` | Heritage, a Rīga protection area or a red-line finding requires review. |
| `red` | `insufficient_data` | The address is absent or ambiguous, so the spatial check cannot be completed. |

Current spatial flags used by rules are nationwide heritage proximity/intersection,
ĪADT, and in the full Rīga import: cultural-heritage protection areas, urban
heritage monuments, cultural-historic territories, building-protection areas,
coastal protection zones, microreserves, flood-risk areas and red lines.
Functional zoning is imported and returned as a finding for audit, but it does
not yet determine an automated permission because the required project dimensions
and local-plan rules are not fully normalised.

`rule-engine-without-ai.md` explains the implemented model, source snapshot
inventory and the boundary between the MVP rules and the next legal-review stage.

## API

- `GET /api/session/mock`
- `GET /api/intents`
- `GET /api/addresses/search?q=...`
- `POST /api/precheck`
- `GET /api/precheck/{requestId}/summary`
- `GET /openapi/v1.json`

`POST /api/precheck` accepts the existing `{ intentId, addressId }` request and
optionally `project` (`placement`, `areaSquareMeters`, `heightMeters`,
`floorCount`, `changesFacade`) and `checkedOn`. The optional fields are recorded
for audit now; they become decision inputs only when a reviewed rule explicitly
uses them.

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

The default Docker Compose stack loads the small demo dataset in `data/demo`.
The separately launched full-data stack loads official local snapshots from
`.local-data/sources`:

- VZD State Address Register: active, confirmed address points nationwide;
- VZD cadastral parcel and parcel-part geometry plus regional encumbrance CSVs;
- NKMP state-protected immovable-monument register, joined to cadastral geometry
  only when its cadastral designation is valid and present in the source data;
- DAP/ĢeoLatvija protected nature-area polygons;
- Rīga territory-plan functional zoning and encumbrance polygons;
- Rīga historical-centre and protection-zone planning polygons.
- Rīga street red lines from the official GeoPackage and selected RTP 2030
  ArcGIS protection-zone GeoJSON layers.
- Planning-region to administrative-territory mappings.

The importer preserves TAPIS and flood-risk WMS metadata in `source_catalog`.
They are marked `reference-only`: the supplied TAPIS extract is partial and the
flood files are WMS capability documents, not vector features suitable for a
deterministic PostGIS intersection.

The demo contains only curated test geometry. It makes the stack reproducible
without publishing 2 GB of raw snapshots; it is never presented as a substitute
for the official full-data import. The full source inventory, snapshot hashes
and official links are stored in `new_data/external/manifest.json` locally and
the normatively relevant hashes are seeded in `legal_source_versions`.
