using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

var rawConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__vaidrikstu")
    ?? Environment.GetEnvironmentVariable("VAIDRIKSTU_CONNECTION_STRING")
    ?? "Host=localhost;Port=5432;Database=vaidrikstu;Username=postgres;Password=postgres";

var connectionString = new NpgsqlConnectionStringBuilder(rawConnectionString)
{
    CommandTimeout = 0
}.ConnectionString;

var sourceRoot = Environment.GetEnvironmentVariable("DATA_SOURCES_PATH")
    ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data", "sources"));
var ruleDataRoot = Environment.GetEnvironmentVariable("DATA_RULE_SOURCES_PATH");
var importProfile = Environment.GetEnvironmentVariable("IMPORT_PROFILE") ?? "national";
var isRegionalProfile = string.Equals(importProfile, "regional", StringComparison.OrdinalIgnoreCase);
var addressScopeTerms = (Environment.GetEnvironmentVariable("ADDRESS_SCOPE_TERMS") ?? string.Empty)
    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

if (!isRegionalProfile && !string.Equals(importProfile, "national", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("IMPORT_PROFILE must be either 'national' or 'regional'.");
}

if (isRegionalProfile && addressScopeTerms.Length == 0)
{
    throw new InvalidOperationException("Regional import requires ADDRESS_SCOPE_TERMS.");
}

Console.WriteLine($"VaiDrikstu.DataImporter starting. Source root: {sourceRoot}");
Console.WriteLine($"Rule-data root: {ruleDataRoot ?? "not configured"}");
Console.WriteLine($"Import profile: {importProfile}. Address scope: {(addressScopeTerms.Length == 0 ? "nationwide" : string.Join(", ", addressScopeTerms))}");

var files = SourceFiles.From(sourceRoot);
files.Validate();

await using var connection = new NpgsqlConnection(connectionString);
await connection.OpenAsync();

await EnsureSchemaAsync(connection);
await EnsureSpatialImportSucceededAsync(connection, isRegionalProfile);
await ImportAddressesAsync(connection, files.AddressRegistry, addressScopeTerms);
await ImportPlanningRegionsAsync(connection, files.PlanningRegions);
if (isRegionalProfile)
{
    await RefreshRegionalDerivedDataAsync(connection, addressScopeTerms);
}
else
{
    await ImportHeritageRegistryAsync(connection, files.HeritageRegistry);
    await ImportLandEncumbrancesAsync(connection, files.ParcelEncumbrances, files.ParcelPartEncumbrances);
    await RefreshDerivedDataAsync(connection);
}
await ImportSupplementalSpatialFlagsAsync(connection, ruleDataRoot);
await PrintImportSummaryAsync(connection);

Console.WriteLine("VaiDrikstu.DataImporter completed.");

static async Task EnsureSchemaAsync(NpgsqlConnection connection)
{
    const string sql = """
        create extension if not exists postgis;
        create extension if not exists pg_trgm;

        create table if not exists raw_addresses (
            kods text, tips_cd text, statuss text, apstipr text, apst_pak text,
            vkur_cd text, vkur_tips text, nosaukums text, sort_nos text, atrib text,
            pnod_cd text, dat_sak text, dat_mod text, dat_beig text, for_build text,
            plan_adr text, std text, koord_x text, koord_y text, dd_n text, dd_e text
        );

        create table if not exists raw_heritage_registry (
            objekta_nr text, nosaukums text, tipologiska_grupa text, vertibas_grupa text,
            datejums text, atrasanas_vieta text, stajies_speka text, publiska_pieejamiba text,
            saglabatibas_stavoklis text, apgrutinajuma_kods text, veids text,
            uzturets_atbilstosi_prasibam text, kadastra_apzimejums text, papildus_pazimes text
        );

        create table if not exists raw_land_encumbrances (
            admrkind text, admtkind text, cad_nr text, encd_id text, description text,
            nr text, enc_date text, enc_size text, measure text
        );

        create table if not exists raw_land_encumbrance_parts (
            admrkind text, admtkind text, cad_nr text, encd_id text, description text,
            nr text, enc_date text, enc_size text, measure text
        );

        create table if not exists raw_planning_regions (
            planning_region text,
            administrative_territory text
        );

        create table if not exists addresses (
            id text primary key,
            normalized_address text not null,
            ar_code text not null,
            municipality text not null,
            geom geometry(Point, 4326) not null
        );

        create table if not exists heritage_sites (
            id text primary key,
            name text not null,
            type text not null,
            status text not null,
            source text not null,
            cadastral_code text null,
            geom geometry(Geometry, 4326) not null
        );

        create table if not exists nature_areas (
            id text primary key,
            name text not null,
            category text not null,
            source text not null,
            geom geometry(Geometry, 4326) not null
        );

        create table if not exists land_encumbrances (
            id text primary key,
            cadastral_code text not null,
            restriction_code text not null,
            description text not null,
            municipality text null,
            area_ha numeric null,
            measure text null,
            source_scope text not null
        );

        create table if not exists address_parcels (
            address_id text not null references addresses(id),
            cadastral_code text not null,
            source_kind text not null,
            primary key (address_id, cadastral_code, source_kind)
        );

        create table if not exists planning_zones (
            id text primary key,
            area_name text not null,
            zone_code text null,
            floors text null,
            source text not null,
            geom geometry(Geometry, 4326) not null
        );

        create table if not exists planning_overlays (
            id text primary key,
            name text not null,
            category text not null,
            source text not null,
            geom geometry(Geometry, 4326) not null
        );

        create table if not exists administrative_regions (
            planning_region text not null,
            administrative_territory text not null,
            primary key (planning_region, administrative_territory)
        );

        create table if not exists source_catalog (
            source_key text primary key,
            publisher text not null,
            dataset_name text not null,
            local_path text not null,
            coverage text not null,
            imported_rows bigint null,
            import_mode text not null,
            notes text null,
            imported_at timestamptz not null default now()
        );

        create table if not exists precheck_runs (
            request_id uuid primary key,
            intent_id text not null,
            address_id text null references addresses(id),
            address_label text not null,
            result_level text not null,
            reason_codes jsonb not null,
            result_json jsonb not null,
            created_at timestamptz not null default now()
        );

        create table if not exists spatial_flags (
            id text primary key,
            flag_type text not null,
            name text not null,
            category text not null,
            source_id text not null,
            geom geometry(Geometry, 4326) not null
        );

        alter table heritage_sites add column if not exists source text not null default 'unknown';
        alter table heritage_sites add column if not exists cadastral_code text null;
        alter table nature_areas alter column geom type geometry(Geometry, 4326);
        alter table precheck_runs add column if not exists regulatory_status text not null default 'no_known_blocker';
        alter table precheck_runs add column if not exists coverage_warnings jsonb not null default '[]'::jsonb;
        alter table precheck_runs add column if not exists applied_rules jsonb not null default '[]'::jsonb;
        alter table precheck_runs add column if not exists project_parameters jsonb not null default '{}'::jsonb;
        alter table precheck_runs add column if not exists checked_on date not null default current_date;

        create index if not exists ix_addresses_geom on addresses using gist (geom);
        create index if not exists ix_addresses_search on addresses using gin (normalized_address gin_trgm_ops);
        create index if not exists ix_heritage_sites_geom on heritage_sites using gist (geom);
        create index if not exists ix_nature_areas_geom on nature_areas using gist (geom);
        create index if not exists ix_land_encumbrances_cadastral_code on land_encumbrances(cadastral_code);
        create index if not exists ix_address_parcels_address_id on address_parcels(address_id);
        create index if not exists ix_planning_zones_geom on planning_zones using gist (geom);
        create index if not exists ix_planning_overlays_geom on planning_overlays using gist (geom);
        create index if not exists ix_spatial_flags_geom on spatial_flags using gist (geom);
        """;

    await ExecuteAsync(connection, sql);
}

static async Task EnsureSpatialImportSucceededAsync(NpgsqlConnection connection, bool isRegionalProfile)
{
    var requiredTables = new List<string>
    {
        "raw_nature_areas", "raw_riga_zoning", "raw_riga_encumbrances", "raw_rvc_core",
        "raw_rvc_protection", "raw_rvc_character_areas"
    };

    if (!isRegionalProfile)
    {
        requiredTables.AddRange(["raw_cadastral_parcels", "raw_cadastral_parcel_parts"]);
    }

    foreach (var table in requiredTables)
    {
        await using var command = new NpgsqlCommand("select to_regclass(@table) is not null;", connection);
        command.Parameters.AddWithValue("table", table);
        if (await command.ExecuteScalarAsync() is not true)
        {
            throw new InvalidOperationException(
                $"Missing '{table}'. The geo-importer must complete successfully before DataImporter starts.");
        }
    }

    if (isRegionalProfile)
    {
        await ExecuteAsync(connection, """
            drop table if exists raw_cadastral_parcels;
            drop table if exists raw_cadastral_parcel_parts;
            truncate table raw_heritage_registry, raw_land_encumbrances, raw_land_encumbrance_parts;
            """);
        Console.WriteLine("Regional profile: skipping national cadastral geometry and encumbrance imports.");
        return;
    }

    await ExecuteAsync(connection, """
        create index if not exists ix_raw_cadastral_parcels_code on raw_cadastral_parcels(code);
        create index if not exists ix_raw_cadastral_parcel_parts_code on raw_cadastral_parcel_parts(code);
        create index if not exists ix_raw_cadastral_parcels_geom on raw_cadastral_parcels using gist (geom);
        create index if not exists ix_raw_cadastral_parcel_parts_geom on raw_cadastral_parcel_parts using gist (geom);
        """);

    var parcelCount = await CountAsync(connection, "raw_cadastral_parcels");
    if (parcelCount < 1_000_000)
    {
        throw new InvalidOperationException(
            $"Cadastral import is incomplete ({parcelCount:N0} parcels). Expected the full national archive before normalizing data.");
    }
}

static async Task ImportAddressesAsync(NpgsqlConnection connection, string path, IReadOnlyList<string> scopeTerms)
{
    await ExecuteAsync(connection, "truncate table precheck_runs, address_parcels, heritage_sites, nature_areas, spatial_flags, land_encumbrances, planning_zones, planning_overlays, administrative_regions, source_catalog, addresses, raw_addresses;");
    await CopyCsvAsync(connection, "raw_addresses", path, ',');

    const string sql = """
        insert into addresses (id, normalized_address, ar_code, municipality, geom)
        select
            kods,
            trim(std),
            kods,
            case
                when std ilike '%, Rīga%' then 'Rīga'
                when std ilike '%, Jūrmala%' then 'Jūrmala'
                when std ilike '%, Daugavpils%' then 'Daugavpils'
                when std ilike '%, Liepāja%' then 'Liepāja'
                when std ilike '%, Valmiera%' then 'Valmiera'
                when std ~ ',\s*[^,]+ nov\.' then (regexp_match(std, ',\s*([^,]+ nov\.)'))[1]
                else 'Latvija'
            end,
            st_setsrid(st_makepoint(dd_e::double precision, dd_n::double precision), 4326)
        from raw_addresses
        where statuss = 'EKS'
          and apstipr = 'Y'
          and nullif(dd_n, '')::double precision between 55 and 59
          and nullif(dd_e, '')::double precision between 20 and 29
        """;

    var scopeClause = scopeTerms.Count == 0 ? ";" : " and std ilike any(@scopePatterns);";
    await using (var command = new NpgsqlCommand(sql + scopeClause, connection))
    {
        if (scopeTerms.Count > 0)
        {
            command.Parameters.AddWithValue(
                "scopePatterns",
                NpgsqlDbType.Array | NpgsqlDbType.Text,
                scopeTerms.Select(term => $"%{term}%").ToArray());
        }

        await command.ExecuteNonQueryAsync();
    }
    var count = await CountAsync(connection, "addresses");
    var minimumExpected = scopeTerms.Count == 0 ? 100_000 : 1_000;
    if (count < minimumExpected)
    {
        throw new InvalidOperationException($"Address import produced only {count} records; expected at least {minimumExpected} official VZD addresses.");
    }

    Console.WriteLine($"Imported {count:N0} official VZD addresses{(scopeTerms.Count == 0 ? string.Empty : " in the configured regional scope")}.");
}

static async Task ImportHeritageRegistryAsync(NpgsqlConnection connection, string path)
{
    await ExecuteAsync(connection, "truncate table raw_heritage_registry;");
    await CopyCsvAsync(connection, "raw_heritage_registry", path, ';');
    Console.WriteLine($"Imported {await CountAsync(connection, "raw_heritage_registry"):N0} NKMP registry records.");
}

static async Task ImportPlanningRegionsAsync(NpgsqlConnection connection, string path)
{
    await ExecuteAsync(connection, "truncate table raw_planning_regions;");
    await CopyCsvAsync(connection, "raw_planning_regions", path, ';');
    Console.WriteLine($"Imported {await CountAsync(connection, "raw_planning_regions"):N0} planning-region mappings.");
}

static async Task ImportLandEncumbrancesAsync(
    NpgsqlConnection connection,
    IReadOnlyList<string> parcelFiles,
    IReadOnlyList<string> parcelPartFiles)
{
    await ExecuteAsync(connection, "truncate table raw_land_encumbrances, raw_land_encumbrance_parts;");

    foreach (var path in parcelFiles)
    {
        await CopyCsvAsync(connection, "raw_land_encumbrances", path, ',');
    }

    foreach (var path in parcelPartFiles)
    {
        await CopyCsvAsync(connection, "raw_land_encumbrance_parts", path, ',');
    }

    Console.WriteLine(
        $"Imported {await CountAsync(connection, "raw_land_encumbrances"):N0} land-parcel and " +
        $"{await CountAsync(connection, "raw_land_encumbrance_parts"):N0} parcel-part encumbrances.");
}

static async Task RefreshDerivedDataAsync(NpgsqlConnection connection)
{
    const string sql = """
        -- step
        insert into nature_areas (id, name, category, source, geom)
        select
            'IADT:' || id::text,
            coalesce(nullif(name, ''), 'Īpaši aizsargājama dabas teritorija'),
            coalesce(nullif(category, ''), 'Nav norādīta kategorija'),
            'DAP / ĢeoLatvija IADT',
            st_makevalid(geom)
        from raw_nature_areas
        where geom is not null and not st_isempty(geom);

        -- step
        insert into land_encumbrances (id, cadastral_code, restriction_code, description, municipality, area_ha, measure, source_scope)
        select distinct on (source_scope, cad_nr, encd_id, nr)
            source_scope || ':' || cad_nr || ':' || encd_id || ':' || nr,
            cad_nr,
            encd_id,
            description,
            nullif(admrkind, ''),
            nullif(enc_size, '')::numeric,
            nullif(measure, ''),
            source_scope
        from (
            select *, 'land-parcel'::text as source_scope from raw_land_encumbrances
            union all
            select *, 'parcel-part'::text as source_scope from raw_land_encumbrance_parts
        ) encumbrances
        where cad_nr ~ '^[0-9]{11,15}$'
          and nullif(description, '') is not null
        order by source_scope, cad_nr, encd_id, nr;

        -- step
        insert into heritage_sites (id, name, type, status, source, cadastral_code, geom)
        with registry_links as (
            select distinct
                h.objekta_nr,
                h.nosaukums,
                h.tipologiska_grupa,
                h.vertibas_grupa,
                regexp_replace(part.value, '[^0-9]', '', 'g') as cadastral_code
            from raw_heritage_registry h
            cross join lateral regexp_split_to_table(coalesce(h.kadastra_apzimejums, ''), '[[:space:]]*;[[:space:]]*') as part(value)
        ), parcel_geometries as (
            select code as cadastral_code, geom from raw_cadastral_parcels
            union all
            select code as cadastral_code, geom from raw_cadastral_parcel_parts
        )
        select distinct on (links.objekta_nr, links.cadastral_code)
            'NKMP:' || links.objekta_nr || ':' || links.cadastral_code,
            coalesce(nullif(links.nosaukums, ''), 'Valsts aizsargājams kultūras piemineklis'),
            coalesce(nullif(links.tipologiska_grupa, ''), 'Kultūras piemineklis'),
            coalesce(nullif(links.vertibas_grupa, ''), 'Valsts aizsargājams'),
            'NKMP Valsts aizsargājamo nekustamo pieminekļu saraksts',
            links.cadastral_code,
            st_makevalid(geometries.geom)
        from registry_links links
        join parcel_geometries geometries on geometries.cadastral_code = links.cadastral_code
        where links.cadastral_code ~ '^[0-9]{11,15}$'
          and geometries.geom is not null
        order by links.objekta_nr, links.cadastral_code;

        -- step
        insert into heritage_sites (id, name, type, status, source, cadastral_code, geom)
        with parcel_geometries as (
            select code as cadastral_code, geom from raw_cadastral_parcels
            union all
            select code as cadastral_code, geom from raw_cadastral_parcel_parts
        )
        select distinct on (encumbrance.id)
            'VZD:' || encumbrance.id,
            encumbrance.description,
            'Kadastra apgrūtinājums',
            encumbrance.restriction_code,
            'VZD Kadastra informācijas sistēmas atvērtie dati',
            encumbrance.cadastral_code,
            st_makevalid(geometries.geom)
        from land_encumbrances encumbrance
        join parcel_geometries geometries on geometries.cadastral_code = encumbrance.cadastral_code
        where encumbrance.description ilike '%kultūras pieminek%'
          and geometries.geom is not null
        order by encumbrance.id;

        -- step
        insert into address_parcels (address_id, cadastral_code, source_kind)
        select distinct a.id, p.code, 'parcel'
        from addresses a
        join raw_cadastral_parcels p on st_intersects(a.geom, p.geom)
        union
        select distinct a.id, p.code, 'parcel-part'
        from addresses a
        join raw_cadastral_parcel_parts p on st_intersects(a.geom, p.geom);

        -- step
        insert into planning_zones (id, area_name, zone_code, floors, source, geom)
        select
            'RTP-FZ:' || fid::text,
            'Rīgas teritorijas plānojuma funkcionālais zonējums',
            planotais_,
            "stāvi",
            'Rīgas valstspilsētas pašvaldība',
            st_makevalid(geom)
        from raw_riga_zoning
        where geom is not null and not st_isempty(geom);

        -- step
        insert into spatial_flags (id, flag_type, name, category, source_id, geom)
        select
            'planning-zone:' || id,
            'planning_zone',
            area_name,
            coalesce(zone_code, 'Nav norādīts'),
            'riga-territory-plan',
            geom
        from planning_zones;

        -- step
        insert into planning_overlays (id, name, category, source, geom)
        select 'RTP-ENC:' || fid::text,
               coalesce(nullif(veids, ''), 'Teritorija ar apgrūtinājumu'),
               coalesce(nullif(grupa_nos, ''), nullif(kods, ''), 'Nav norādīta grupa'),
               'Rīgas valstspilsētas pašvaldība',
               st_makevalid(geom)
        from raw_riga_encumbrances
        where geom is not null and not st_isempty(geom);

        -- step
        insert into planning_overlays (id, name, category, source, geom)
        select 'RVC-7442:' || objectid::text,
               'Rīgas vēsturiskais centrs',
               'RVC robeža',
               'Rīgas vēsturiskā centra un tā aizsardzības zonas teritorijas plānojums',
               st_makevalid(geom)
        from raw_rvc_core;

        -- step
        insert into planning_overlays (id, name, category, source, geom)
        select 'RVC-8327:' || objectid::text,
               'Rīgas vēsturiskā centra aizsardzības zona',
               'RVC aizsardzības zona',
               'Rīgas vēsturiskā centra un tā aizsardzības zonas teritorijas plānojums',
               st_makevalid(geom)
        from raw_rvc_protection;

        -- step
        insert into planning_overlays (id, name, category, source, geom)
        select 'RVC-CHAR:' || objectid::text,
               'Kultūrvēsturiski nozīmīga autentiska pilsētvides teritorija',
               'RVC apbūves veidošanas pamatnosacījumi',
               'Rīgas vēsturiskā centra un tā aizsardzības zonas teritorijas plānojums',
               st_makevalid(geom)
        from raw_rvc_character_areas;

        -- step
        insert into administrative_regions (planning_region, administrative_territory)
        select distinct trim(planning_region), trim(administrative_territory)
        from raw_planning_regions
        where nullif(trim(planning_region), '') is not null
          and nullif(trim(administrative_territory), '') is not null;

        -- step
        insert into source_catalog (source_key, publisher, dataset_name, local_path, coverage, imported_rows, import_mode, notes)
        values
            ('vzd-addresses', 'Valsts zemes dienests', 'Valsts adrešu reģistra atvērtie dati', 'data/aw_eka.csv', 'Latvija', (select count(*) from addresses), 'loaded', 'Adrešu punktu centroidi.'),
            ('dap-iadt', 'Dabas aizsardzības pārvalde / ĢeoLatvija', 'Īpaši aizsargājamās dabas teritorijas', 'Ipasi_aizsargajamas_dabas_teritorijas/IADT_pamatteritorijas.shp', 'Latvija', (select count(*) from nature_areas), 'loaded', 'Poligoni pārveidoti uz EPSG:4326.'),
            ('vzd-cadastre', 'Valsts zemes dienests', 'Kadastra informācijas sistēmas atvērtie telpiskie dati', 'data/*_kk_shp.zip', 'Latvija', (select count(*) from raw_cadastral_parcels), 'loaded', 'Kadastra zemes vienību un zemes vienību daļu ģeometrija.'),
            ('nkmp-monuments', 'Nacionālā kultūras mantojuma pārvalde', 'Valsts aizsargājamo nekustamo pieminekļu saraksts', 'saraksts.csv', 'Latvija', (select count(*) from heritage_sites where source like 'NKMP%'), 'loaded', 'Saistīts ar kadastra ģeometriju tikai tad, ja avotā ir derīgs kadastra apzīmējums.'),
            ('vzd-encumbrances', 'Valsts zemes dienests', 'Kadastra apgrūtinājumi', 'apgrutinajumi_zemes_vienibas_*.csv', 'Latvija', (select count(*) from land_encumbrances), 'loaded', 'Tabulāri kadastra apgrūtinājumi, sasaistīti ar zemes vienībām.'),
            ('riga-territory-plan', 'Rīgas valstspilsētas pašvaldība', 'Rīgas teritorijas plānojums', 'downloaded/rigas-teritorijas-planojums', 'Rīga', (select count(*) from planning_zones), 'loaded', 'Funkcionālais zonējums un teritorijas ar apgrūtinājumiem.'),
            ('rvc-territory-plan', 'Rīgas valstspilsētas pašvaldība', 'Rīgas vēsturiskā centra un tā aizsardzības zonas teritorijas plānojums', 'downloaded/rigas-vesturiskais-centrs', 'Rīga', (select count(*) from planning_overlays where source like 'Rīgas vēsturiskā centra%'), 'loaded', 'RVC, aizsardzības zonas un pilsētvides teritorijas.'),
            ('planning-regions', 'Viedās administrācijas un reģionālās attīstības ministrija', 'Plānošanas reģioni un administratīvās teritorijas', 'planosanas_regioni_administrativas_teritorijas.csv', 'Latvija', (select count(*) from administrative_regions), 'loaded', 'Administratīvo teritoriju piederība plānošanas reģionam.'),
            ('tapis-wfs', 'Viedās administrācijas un reģionālās attīstības ministrija', 'TAPIS ģeotelpiskie dati WFS', 'downloaded/tapis', 'Daļējs Rīgas WFS izraksts', null, 'reference-only', 'WFS izraksts ir nepilnīgs, tāpēc netiek izmantots automātiskā pārbaudē.'),
            ('flood-wms', 'Latvijas Vides, ģeoloģijas un meteoroloģijas centrs', 'Plūdu riska kartes', 'downloaded/pludi', 'Latvija', null, 'reference-only', 'Piegādāts tikai WMS capabilities XML, nevis vektoru dati telpiskai pārbaudei.')
        on conflict (source_key) do update set
            publisher = excluded.publisher,
            dataset_name = excluded.dataset_name,
            local_path = excluded.local_path,
            coverage = excluded.coverage,
            imported_rows = excluded.imported_rows,
            import_mode = excluded.import_mode,
            notes = excluded.notes,
            imported_at = now();
        """;

    var steps = sql.Split("-- step", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    for (var index = 0; index < steps.Length; index++)
    {
        Console.WriteLine($"Refreshing derived data step {index + 1}/{steps.Length}.");
        await ExecuteAsync(connection, steps[index]);
    }
}

static async Task RefreshRegionalDerivedDataAsync(NpgsqlConnection connection, IReadOnlyList<string> scopeTerms)
{
    const string sql = """
        -- step
        insert into nature_areas (id, name, category, source, geom)
        select
            'IADT:' || id::text,
            coalesce(nullif(name, ''), 'Īpaši aizsargājama dabas teritorija'),
            coalesce(nullif(category, ''), 'Nav norādīta kategorija'),
            'DAP / ĢeoLatvija IADT',
            st_makevalid(geom)
        from raw_nature_areas
        where geom is not null and not st_isempty(geom);

        -- step
        insert into planning_zones (id, area_name, zone_code, floors, source, geom)
        select
            'RTP-FZ:' || fid::text,
            'Rīgas teritorijas plānojuma funkcionālais zonējums',
            planotais_,
            "stāvi",
            'Rīgas valstspilsētas pašvaldība',
            st_makevalid(geom)
        from raw_riga_zoning
        where geom is not null and not st_isempty(geom);

        -- step
        insert into spatial_flags (id, flag_type, name, category, source_id, geom)
        select
            'planning-zone:' || id,
            'planning_zone',
            area_name,
            coalesce(zone_code, 'Nav norādīts'),
            'riga-territory-plan',
            geom
        from planning_zones;

        -- step
        insert into planning_overlays (id, name, category, source, geom)
        select 'RTP-ENC:' || fid::text,
               coalesce(nullif(veids, ''), 'Teritorija ar apgrūtinājumu'),
               coalesce(nullif(grupa_nos, ''), nullif(kods, ''), 'Nav norādīta grupa'),
               'Rīgas valstspilsētas pašvaldība',
               st_makevalid(geom)
        from raw_riga_encumbrances
        where geom is not null and not st_isempty(geom);

        -- step
        insert into planning_overlays (id, name, category, source, geom)
        select 'RVC-7442:' || objectid::text,
               'Rīgas vēsturiskais centrs',
               'RVC robeža',
               'Rīgas vēsturiskā centra un tā aizsardzības zonas teritorijas plānojums',
               st_makevalid(geom)
        from raw_rvc_core;

        -- step
        insert into planning_overlays (id, name, category, source, geom)
        select 'RVC-8327:' || objectid::text,
               'Rīgas vēsturiskā centra aizsardzības zona',
               'RVC aizsardzības zona',
               'Rīgas vēsturiskā centra un tā aizsardzības zonas teritorijas plānojums',
               st_makevalid(geom)
        from raw_rvc_protection;

        -- step
        insert into planning_overlays (id, name, category, source, geom)
        select 'RVC-CHAR:' || objectid::text,
               'Kultūrvēsturiski nozīmīga autentiska pilsētvides teritorija',
               'RVC apbūves veidošanas pamatnosacījumi',
               'Rīgas vēsturiskā centra un tā aizsardzības zonas teritorijas plānojums',
               st_makevalid(geom)
        from raw_rvc_character_areas;

        -- step
        insert into administrative_regions (planning_region, administrative_territory)
        select distinct trim(planning_region), trim(administrative_territory)
        from raw_planning_regions
        where nullif(trim(planning_region), '') is not null
          and nullif(trim(administrative_territory), '') is not null;

        -- step
        insert into source_catalog (source_key, publisher, dataset_name, local_path, coverage, imported_rows, import_mode, notes)
        values
            ('vzd-addresses', 'Valsts zemes dienests', 'Valsts adrešu reģistra atvērtie dati', 'data/aw_eka.csv', 'Konfigurēts reģionāls izgriezums', (select count(*) from addresses), 'regional', 'Adrešu punktu izgriezums ātram MVP; valsts mēroga kadastrs nav ielādēts.'),
            ('dap-iadt', 'Dabas aizsardzības pārvalde / ĢeoLatvija', 'Īpaši aizsargājamās dabas teritorijas', 'Ipasi_aizsargajamas_dabas_teritorijas/IADT_pamatteritorijas.shp', 'Latvija', (select count(*) from nature_areas), 'loaded', 'Poligoni pārveidoti uz EPSG:4326.'),
            ('riga-territory-plan', 'Rīgas valstspilsētas pašvaldība', 'Rīgas teritorijas plānojums', 'downloaded/rigas-teritorijas-planojums', 'Rīga', (select count(*) from planning_zones), 'loaded', 'Funkcionālais zonējums un teritorijas ar apgrūtinājumiem.'),
            ('rvc-territory-plan', 'Rīgas valstspilsētas pašvaldība', 'Rīgas vēsturiskā centra un tā aizsardzības zonas teritorijas plānojums', 'downloaded/rigas-vesturiskais-centrs', 'Rīga', (select count(*) from planning_overlays where source like 'Rīgas vēsturiskā centra%'), 'loaded', 'RVC, aizsardzības zonas un pilsētvides teritorijas.'),
            ('vzd-cadastre', 'Valsts zemes dienests', 'Kadastra informācijas sistēmas atvērtie telpiskie dati', 'data/*_kk_shp.zip', 'Nav ielādēts regional profilā', null, 'not-loaded', 'Ātrā profila ierobežojums: nav kadastra ģeometrijas, apgrūtinājumu un ar kadastru sasaistītā NKMP slāņa.'),
            ('tapis-wfs', 'Viedās administrācijas un reģionālās attīstības ministrija', 'TAPIS ģeotelpiskie dati WFS', 'downloaded/tapis', 'Daļējs Rīgas WFS izraksts', null, 'reference-only', 'WFS izraksts ir nepilnīgs, tāpēc netiek izmantots automātiskā pārbaudē.')
        on conflict (source_key) do update set
            publisher = excluded.publisher,
            dataset_name = excluded.dataset_name,
            local_path = excluded.local_path,
            coverage = excluded.coverage,
            imported_rows = excluded.imported_rows,
            import_mode = excluded.import_mode,
            notes = excluded.notes,
            imported_at = now();
        """;

    var steps = sql.Split("-- step", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    for (var index = 0; index < steps.Length; index++)
    {
        Console.WriteLine($"Refreshing regional data step {index + 1}/{steps.Length}.");
        await ExecuteAsync(connection, steps[index]);
    }

    Console.WriteLine($"Regional address scope refreshed: {string.Join(", ", scopeTerms)}.");
}

static async Task PrintImportSummaryAsync(NpgsqlConnection connection)
{
    var tables = new[]
    {
        "addresses", "nature_areas", "raw_cadastral_parcels", "raw_cadastral_parcel_parts",
        "heritage_sites", "land_encumbrances", "address_parcels", "planning_zones", "planning_overlays",
        "administrative_regions", "spatial_flags"
    };

    foreach (var table in tables)
    {
        Console.WriteLine(await RelationExistsAsync(connection, table)
            ? $"{table}: {await CountAsync(connection, table):N0}"
            : $"{table}: not loaded");
    }
}

static async Task ImportSupplementalSpatialFlagsAsync(NpgsqlConnection connection, string? ruleDataRoot)
{
    if (string.IsNullOrWhiteSpace(ruleDataRoot) || !Directory.Exists(ruleDataRoot))
    {
        Console.WriteLine("Supplemental Rīga rule data not available; retaining core nationwide findings.");
        return;
    }

    var layerDirectory = Path.Combine(ruleDataRoot, "02-additional-geodata", "12-riga-protection-zones-arcgis");
    var layers = new[]
    {
        new SpatialLayer(3, "heritage_protection_zone", "Individuālā aizsardzības zona ap kultūras pieminekli"),
        new SpatialLayer(4, "heritage_protection_zone", "Aizsargjosla ap kultūras pieminekli"),
        new SpatialLayer(6, "urban_heritage_monument", "Valsts aizsargājams pilsētbūvniecības piemineklis"),
        new SpatialLayer(7, "cultural_historic_territory", "Kultūrvēsturiskā teritorija"),
        new SpatialLayer(9, "coastal_protection_zone", "Jūras aizsargjosla"),
        new SpatialLayer(10, "coastal_protection_zone", "Krasta kāpu aizsargjosla"),
        new SpatialLayer(57, "building_protection_area", "Apbūves aizsardzības teritorija"),
        new SpatialLayer(58, "microreserve", "Mikroliegums"),
        new SpatialLayer(59, "protected_nature_area", "Īpaši aizsargājama dabas teritorija"),
        new SpatialLayer(60, "flood_risk", "Applūšanas riska teritorija")
    };

    foreach (var layer in layers)
    {
        var files = Directory.GetFiles(layerDirectory, $"layer-{layer.Id:D2}-*.geojson");
        if (files.Length != 1)
        {
            Console.WriteLine($"Skipping rule layer {layer.Id}: expected one GeoJSON export, found {files.Length}.");
            continue;
        }

        using var stream = File.OpenRead(files[0]);
        using var document = await JsonDocument.ParseAsync(stream);
        var featureIndex = 0;
        foreach (var feature in document.RootElement.GetProperty("features").EnumerateArray())
        {
            if (!feature.TryGetProperty("geometry", out var geometry) || geometry.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            featureIndex++;
            var name = ReadFeatureName(feature, layer.Name);
            await using var command = new NpgsqlCommand("""
                insert into spatial_flags (id, flag_type, name, category, source_id, geom)
                values (@id, @flagType, @name, @category, 'riga-protection-zones',
                        st_setsrid(st_makevalid(st_geomfromgeojson(@geometry)), 4326))
                on conflict (id) do update set
                    flag_type = excluded.flag_type,
                    name = excluded.name,
                    category = excluded.category,
                    geom = excluded.geom;
                """, connection);
            command.Parameters.AddWithValue("id", $"riga-protection:{layer.Id}:{featureIndex}");
            command.Parameters.AddWithValue("flagType", layer.FlagType);
            command.Parameters.AddWithValue("name", name);
            command.Parameters.AddWithValue("category", layer.Name);
            command.Parameters.AddWithValue("geometry", geometry.GetRawText());
            await command.ExecuteNonQueryAsync();
        }

        Console.WriteLine($"Imported {featureIndex:N0} spatial flags from {Path.GetFileName(files[0])}.");
    }

    var hasRedLines = await RelationExistsAsync(connection, "raw_riga_red_lines");
    if (hasRedLines)
    {
        await ExecuteAsync(connection, """
            insert into spatial_flags (id, flag_type, name, category, source_id, geom)
            select
                'riga-red-line:' || row_number() over (),
                'red_line',
                'Rīgas ielas sarkanā līnija',
                'Sarkanā līnija',
                'riga-red-lines',
                st_makevalid(geom)
            from raw_riga_red_lines
            where geom is not null and not st_isempty(geom);
            """);
    }
}

static string ReadFeatureName(JsonElement feature, string fallback)
{
    if (!feature.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
    {
        return fallback;
    }

    foreach (var key in new[] { "NAME", "name", "title", "ZoneName", "kods", "Kods" })
    {
        if (properties.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
        {
            return value.GetString()!;
        }
    }

    return fallback;
}

static async Task<bool> RelationExistsAsync(NpgsqlConnection connection, string table)
{
    await using var command = new NpgsqlCommand("select to_regclass(@table) is not null;", connection);
    command.Parameters.AddWithValue("table", table);
    return await command.ExecuteScalarAsync() is true;
}

static async Task CopyCsvAsync(NpgsqlConnection connection, string table, string path, char delimiter)
{
    Console.WriteLine($"Loading {Path.GetFileName(path)} into {table}.");
    using var reader = new StreamReader(path, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
    await using var writer = await connection.BeginTextImportAsync(
        $"copy {table} from stdin with (format csv, header true, delimiter '{delimiter}', encoding 'UTF8')");
    var buffer = new char[64 * 1024];
    int read;
    while ((read = await reader.ReadAsync(buffer.AsMemory())) > 0)
    {
        await writer.WriteAsync(buffer.AsMemory(0, read));
    }
}

static async Task<long> CountAsync(NpgsqlConnection connection, string table)
{
    await using var command = new NpgsqlCommand($"select count(*) from {table};", connection);
    return (long)(await command.ExecuteScalarAsync() ?? 0L);
}

static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
{
    await using var command = new NpgsqlCommand(sql, connection);
    await command.ExecuteNonQueryAsync();
}

sealed record SpatialLayer(int Id, string FlagType, string Name);

sealed record SourceFiles(
    string AddressRegistry,
    string HeritageRegistry,
    string PlanningRegions,
    IReadOnlyList<string> ParcelEncumbrances,
    IReadOnlyList<string> ParcelPartEncumbrances)
{
    public static SourceFiles From(string root) => new(
        Path.Combine(root, "data", "aw_eka.csv"),
        Path.Combine(root, "saraksts.csv"),
        Path.Combine(root, "planosanas_regioni_administrativas_teritorijas.csv"),
        Directory.GetFiles(root, "apgrutinajumi_zemes_vienibas_*.csv").Order(StringComparer.Ordinal).ToArray(),
        Directory.GetFiles(root, "apgrutinajumi_zemes_vienibu_dalas_*.csv").Order(StringComparer.Ordinal).ToArray());

    public void Validate()
    {
        foreach (var path in new[] { AddressRegistry, HeritageRegistry, PlanningRegions })
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Required official source file is missing: {path}", path);
            }
        }

        if (ParcelEncumbrances.Count != 5 || ParcelPartEncumbrances.Count != 5)
        {
            throw new InvalidOperationException(
                "Expected five regional land-parcel and five parcel-part encumbrance CSV files.");
        }
    }
}
