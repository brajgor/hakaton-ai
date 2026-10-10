# Coolify deployment

This setup deploys `building-precheck` behind one public domain:

- `web` serves the React/Vite build with nginx.
- `web` proxies `/api/*` to the internal `api:8080` service.
- `api` talks to the internal `postgis` service.
- `postgis-data` is the only runtime persistent volume.
- `demo-seed`, `geo-importer`, and `data-importer` are one-shot profile services.

Use `docker-compose.coolify.yml` as the Coolify Docker Compose file.

## Runtime variables

Set these in Coolify:

```env
POSTGRES_DB=vaidrikstu
POSTGRES_USER=postgres
SERVICE_PASSWORD_POSTGRES=<generate in Coolify>
VITE_API_URL=
```

`VITE_API_URL` must stay empty for same-domain deployment. The browser will call
relative `/api/...` URLs, and nginx will proxy them to the API container.

## Optional import variables

Use these only when running the import profile. The default Coolify import is
the full national import:

```env
DATA_SOURCES_HOST_PATH=/data/vaidrikstu/sources
RULE_DATA_HOST_PATH=/data/vaidrikstu/rule-data
IMPORT_PROFILE=national
ADDRESS_SCOPE_TERMS=
```

The source mounts must contain the same layout as the local ignored folders:

- `DATA_SOURCES_HOST_PATH` = local `building-precheck/.local-data/sources`
- `RULE_DATA_HOST_PATH` = local `building-precheck/new_data`

The import rewrites the imported tables and truncates `precheck_runs`. Run it
only before going live or when you intentionally refresh the open-data snapshot.

## One-shot commands

After the stack has been deployed once, use the generated Compose directory on
the server.

Demo data:

```bash
docker compose --profile demo run --rm demo-seed
docker compose up -d api web
```

Full national import:

```bash
docker compose --profile import run --rm data-importer
docker compose up -d api web
```

Regional real-data import:

```bash
IMPORT_PROFILE=regional ADDRESS_SCOPE_TERMS='Rīga,;Jūrmala,;Liepāja,;Daugavpils,;Valmiera,' docker compose --profile import run --rm data-importer
docker compose up -d api web
```

## Checks

After deployment:

```bash
curl -fsS https://hackaton2026.brajgor.dev/health
curl -fsS https://hackaton2026.brajgor.dev/api/intents
```

Open `https://hackaton2026.brajgor.dev` and search for an address included in
the selected import scope.
