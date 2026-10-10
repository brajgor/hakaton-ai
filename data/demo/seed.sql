create extension if not exists postgis;
create extension if not exists pg_trgm;

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

create index if not exists ix_addresses_search on addresses using gin (normalized_address gin_trgm_ops);
create index if not exists ix_heritage_sites_geom on heritage_sites using gist (geom);
create index if not exists ix_nature_areas_geom on nature_areas using gist (geom);

truncate table precheck_runs, heritage_sites, nature_areas, addresses;

insert into addresses (id, normalized_address, ar_code, municipality, geom) values
    ('addr-clean-valmiera', 'Zvaigžņu iela 1, Valmiera, Valmieras nov.', 'DEMO-AR-100001', 'Valmieras novads', st_setsrid(st_makepoint(25.4262, 57.5389), 4326)),
    ('addr-heritage-riga', 'Pils laukums 3, Rīga', 'DEMO-AR-100002', 'Rīga', st_setsrid(st_makepoint(24.1059, 56.9507), 4326)),
    ('addr-nature-jurmala', 'Meža prospekts 1, Jūrmala', 'DEMO-AR-100003', 'Jūrmala', st_setsrid(st_makepoint(23.9231, 56.9908), 4326));

insert into heritage_sites (id, name, type, status, source, geom) values
    ('nkmp-demo-riga-castle-area', 'Rīgas pils kultūrvēsturiskā teritorija (demo izgriezums)', 'Valsts aizsargājams kultūras piemineklis', 'Aizsargājams', 'NKMP demo izgriezums', st_geomfromtext('POLYGON((24.1048 56.9501,24.1070 56.9501,24.1070 56.9514,24.1048 56.9514,24.1048 56.9501))', 4326));

insert into nature_areas (id, name, category, source, geom) values
    ('dap-demo-jurmala-dune-zone', 'Īpaši aizsargājama piekrastes dabas teritorija (demo izgriezums)', 'ĪADT / Natura 2000 pārbaudes slānis', 'DAP demo izgriezums', st_geomfromtext('POLYGON((23.9180 56.9870,23.9290 56.9870,23.9290 56.9940,23.9180 56.9940,23.9180 56.9870))', 4326));
