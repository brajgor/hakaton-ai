using System.Text.Json;
using Npgsql;

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__vaidrikstu")
    ?? Environment.GetEnvironmentVariable("VAIDRIKSTU_CONNECTION_STRING")
    ?? "Host=localhost;Port=5432;Database=vaidrikstu;Username=postgres;Password=postgres";

var sourceRoot = Environment.GetEnvironmentVariable("DATA_SOURCES_PATH")
    ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data", "sources"));

Console.WriteLine($"VaiDrikstu.DataImporter starting. Source root: {sourceRoot}");

await using var connection = new NpgsqlConnection(connectionString);
await connection.OpenAsync();

await EnsureSchemaAsync(connection);
await SeedAddressesAsync(connection, Path.Combine(sourceRoot, "addresses.demo.json"));
await SeedHeritageAsync(connection, Path.Combine(sourceRoot, "heritage.demo.json"));
await SeedNatureAsync(connection, Path.Combine(sourceRoot, "nature.demo.json"));

Console.WriteLine("VaiDrikstu.DataImporter completed.");

static async Task EnsureSchemaAsync(NpgsqlConnection connection)
{
    const string sql = """
        create extension if not exists postgis;

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
            geom geometry(Geometry, 4326) not null
        );

        create table if not exists nature_areas (
            id text primary key,
            name text not null,
            category text not null,
            source text not null,
            geom geometry(Polygon, 4326) not null
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

        create index if not exists ix_addresses_geom on addresses using gist (geom);
        create index if not exists ix_heritage_sites_geom on heritage_sites using gist (geom);
        create index if not exists ix_nature_areas_geom on nature_areas using gist (geom);
        """;

    await using var command = new NpgsqlCommand(sql, connection);
    await command.ExecuteNonQueryAsync();
}

static async Task SeedAddressesAsync(NpgsqlConnection connection, string path)
{
    var rows = ReadRequiredJson<DemoAddress[]>(path);
    await ExecuteAsync(connection, "truncate table addresses cascade;");

    const string sql = """
        insert into addresses (id, normalized_address, ar_code, municipality, geom)
        values (@id, @address, @arCode, @municipality, st_setsrid(st_makepoint(@lon, @lat), 4326));
        """;

    foreach (var row in rows)
    {
        ValidateCoordinate(row.Latitude, row.Longitude, row.Id);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", row.Id);
        command.Parameters.AddWithValue("address", row.NormalizedAddress);
        command.Parameters.AddWithValue("arCode", row.ArCode);
        command.Parameters.AddWithValue("municipality", row.Municipality);
        command.Parameters.AddWithValue("lon", row.Longitude);
        command.Parameters.AddWithValue("lat", row.Latitude);
        await command.ExecuteNonQueryAsync();
    }

    Console.WriteLine($"Imported {rows.Length} demo addresses.");
}

static async Task SeedHeritageAsync(NpgsqlConnection connection, string path)
{
    var rows = ReadRequiredJson<DemoHeritage[]>(path);
    await ExecuteAsync(connection, "truncate table heritage_sites cascade;");

    const string sql = """
        insert into heritage_sites (id, name, type, status, geom)
        values (@id, @name, @type, @status, st_geomfromtext(@wkt, 4326));
        """;

    foreach (var row in rows)
    {
        if (string.IsNullOrWhiteSpace(row.Wkt))
        {
            throw new InvalidOperationException($"Heritage row '{row.Id}' has no WKT geometry.");
        }

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", row.Id);
        command.Parameters.AddWithValue("name", row.Name);
        command.Parameters.AddWithValue("type", row.Type);
        command.Parameters.AddWithValue("status", row.Status);
        command.Parameters.AddWithValue("wkt", row.Wkt);
        await command.ExecuteNonQueryAsync();
    }

    Console.WriteLine($"Imported {rows.Length} demo heritage sites.");
}

static async Task SeedNatureAsync(NpgsqlConnection connection, string path)
{
    var rows = ReadRequiredJson<DemoNatureArea[]>(path);
    await ExecuteAsync(connection, "truncate table nature_areas cascade;");

    const string sql = """
        insert into nature_areas (id, name, category, source, geom)
        values (@id, @name, @category, @source, st_geomfromtext(@wkt, 4326));
        """;

    foreach (var row in rows)
    {
        if (string.IsNullOrWhiteSpace(row.Wkt))
        {
            throw new InvalidOperationException($"Nature row '{row.Id}' has no WKT geometry.");
        }

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", row.Id);
        command.Parameters.AddWithValue("name", row.Name);
        command.Parameters.AddWithValue("category", row.Category);
        command.Parameters.AddWithValue("source", row.Source);
        command.Parameters.AddWithValue("wkt", row.Wkt);
        await command.ExecuteNonQueryAsync();
    }

    Console.WriteLine($"Imported {rows.Length} demo nature areas.");
}

static T ReadRequiredJson<T>(string path)
{
    if (!File.Exists(path))
    {
        throw new FileNotFoundException($"Required source file was not found: {path}", path);
    }

    var json = File.ReadAllText(path);
    return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidOperationException($"Could not parse JSON source: {path}");
}

static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
{
    await using var command = new NpgsqlCommand(sql, connection);
    await command.ExecuteNonQueryAsync();
}

static void ValidateCoordinate(double latitude, double longitude, string id)
{
    if (latitude is < 55 or > 59 || longitude is < 20 or > 29)
    {
        throw new InvalidOperationException($"Address '{id}' has coordinates outside Latvia-like bounds.");
    }
}

public sealed record DemoAddress(
    string Id,
    string NormalizedAddress,
    string ArCode,
    string Municipality,
    double Latitude,
    double Longitude);

public sealed record DemoHeritage(
    string Id,
    string Name,
    string Type,
    string Status,
    string Wkt);

public sealed record DemoNatureArea(
    string Id,
    string Name,
    string Category,
    string Source,
    string Wkt);
