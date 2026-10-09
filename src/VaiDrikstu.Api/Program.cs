using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Npgsql;
using VaiDrikstu.Api;
using VaiDrikstu.Domain;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyHeader()
            .AllowAnyMethod()
            .WithOrigins(
                "http://localhost:5173",
                "https://localhost:5173",
                "http://localhost:3000",
                "https://localhost:3000",
                "http://localhost:8081");
    });
});

var connectionString = builder.Configuration.GetConnectionString("vaidrikstu")
    ?? builder.Configuration.GetConnectionString("Default")
    ?? Environment.GetEnvironmentVariable("ConnectionStrings__vaidrikstu")
    ?? "Host=localhost;Port=5432;Database=vaidrikstu;Username=postgres;Password=postgres";

var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
dataSourceBuilder.UseNetTopologySuite();
builder.Services.AddSingleton(dataSourceBuilder.Build());
builder.Services.AddScoped<PrecheckRepository>();

var app = builder.Build();

app.UseCors();
app.MapOpenApi();
app.MapGet("/swagger", () => Results.Redirect("/openapi/v1.json"));

app.MapGet("/api/session/mock", () => new MockSessionResponse(
        "urn:ivis:100001:name.id-viss:PK:010190-12345",
        "Demo lietotājs",
        "Mock Latvija.lv OIDC"))
    .WithName("GetMockSession")
    .WithOpenApi();

app.MapGet("/api/intents", () => IntentCatalog.All.Select(intent => new IntentResponse(
        intent.Id,
        intent.Title,
        intent.Description)))
    .WithName("GetIntents")
    .WithOpenApi();

app.MapGet("/api/addresses/search", async Task<Results<Ok<AddressResponse[]>, BadRequest<ErrorResponse>>> (
        string q,
        PrecheckRepository repository,
        CancellationToken cancellationToken) =>
    {
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
        {
            return TypedResults.BadRequest(new ErrorResponse("ADDRESS_QUERY_TOO_SHORT", "Ievadiet vismaz 2 simbolus."));
        }

        var addresses = await repository.SearchAddressesAsync(q.Trim(), cancellationToken);
        return TypedResults.Ok(addresses.Select(AddressResponse.FromDomain).ToArray());
    })
    .WithName("SearchAddresses")
    .WithOpenApi();

app.MapPost("/api/precheck", async Task<Results<Ok<PrecheckResponse>, BadRequest<ErrorResponse>>> (
        PrecheckRequest request,
        PrecheckRepository repository,
        CancellationToken cancellationToken) =>
    {
        var intent = IntentCatalog.Find(request.IntentId);
        if (intent is null)
        {
            return TypedResults.BadRequest(new ErrorResponse("UNKNOWN_INTENT", "Izvēlētā darbība nav atpazīta."));
        }

        var address = string.IsNullOrWhiteSpace(request.AddressId)
            ? null
            : await repository.GetAddressAsync(request.AddressId, cancellationToken);

        var findings = address is null
            ? []
            : await repository.GetFindingsAsync(address, cancellationToken);

        var decision = PrecheckRuleEngine.Evaluate(intent, address, findings);
        var requestId = await repository.SavePrecheckRunAsync(request, address, decision, cancellationToken);

        return TypedResults.Ok(PrecheckResponse.FromDecision(requestId, decision));
    })
    .WithName("CreatePrecheck")
    .WithOpenApi();

app.MapGet("/api/precheck/{requestId:guid}/summary", async Task<Results<Ok<PrecheckSummaryResponse>, NotFound<ErrorResponse>>> (
        Guid requestId,
        PrecheckRepository repository,
        CancellationToken cancellationToken) =>
    {
        var summary = await repository.GetSummaryAsync(requestId, cancellationToken);
        return summary is null
            ? TypedResults.NotFound(new ErrorResponse("PRECHECK_NOT_FOUND", "Priekšpārbaudes kopsavilkums nav atrasts."))
            : TypedResults.Ok(summary);
    })
    .WithName("GetPrecheckSummary")
    .WithOpenApi();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "VaiDrikstu.Api" }))
    .WithName("Health")
    .WithOpenApi();

app.Run();

namespace VaiDrikstu.Api
{
    public sealed record MockSessionResponse(string Urn, string DisplayName, string AuthMethod);
    public sealed record IntentResponse(string Id, string Title, string Description);
    public sealed record PrecheckRequest(string IntentId, string AddressId);
    public sealed record ErrorResponse(string Code, string Message);

    public sealed record AddressResponse(
        string Id,
        string NormalizedAddress,
        string ArCode,
        string Municipality,
        double Latitude,
        double Longitude)
    {
        public static AddressResponse FromDomain(AddressRecord address) =>
            new(address.Id, address.NormalizedAddress, address.ArCode, address.Municipality, address.Latitude, address.Longitude);
    }

    public sealed record FindingResponse(
        string Source,
        string Code,
        string Name,
        string Category,
        bool Intersects,
        double? DistanceMeters)
    {
        public static FindingResponse FromDomain(DataFinding finding) =>
            new(
                finding.Source,
                finding.Code,
                finding.Name,
                finding.Category,
                finding.Intersects,
                finding.DistanceMeters is null ? null : Math.Round(finding.DistanceMeters.Value, 1));
    }

    public sealed record PrecheckResponse(
        Guid RequestId,
        string Level,
        string[] ReasonCodes,
        FindingResponse[] DataFindings,
        string[] NextSteps,
        string OfficialDisclaimer)
    {
        public static PrecheckResponse FromDecision(Guid requestId, PrecheckDecision decision) =>
            new(
                requestId,
                decision.Level,
                decision.ReasonCodes,
                decision.DataFindings.Select(FindingResponse.FromDomain).ToArray(),
                decision.NextSteps,
                decision.OfficialDisclaimer);
    }

    public sealed record PrecheckSummaryResponse(
        Guid RequestId,
        string IntentId,
        string AddressLabel,
        string Level,
        string[] ReasonCodes,
        FindingResponse[] DataFindings,
        string[] NextSteps,
        string OfficialDisclaimer,
        DateTimeOffset CreatedAt);

    public sealed class PrecheckRepository(NpgsqlDataSource dataSource)
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public async Task<IReadOnlyList<AddressRecord>> SearchAddressesAsync(string query, CancellationToken cancellationToken)
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            const string sql = """
                select id, normalized_address, ar_code, municipality, st_y(geom) as lat, st_x(geom) as lon
                from addresses
                where normalized_address ilike @query or municipality ilike @query or ar_code ilike @query
                order by normalized_address
                limit 10;
                """;

            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("query", $"%{query}%");

            var result = new List<AddressRecord>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(new AddressRecord(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetDouble(4),
                    reader.GetDouble(5)));
            }

            return result;
        }

        public async Task<AddressRecord?> GetAddressAsync(string addressId, CancellationToken cancellationToken)
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            const string sql = """
                select id, normalized_address, ar_code, municipality, st_y(geom) as lat, st_x(geom) as lon
                from addresses
                where id = @id;
                """;

            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("id", addressId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            return new AddressRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetDouble(4),
                reader.GetDouble(5));
        }

        public async Task<IReadOnlyList<DataFinding>> GetFindingsAsync(AddressRecord address, CancellationToken cancellationToken)
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            const string sql = """
                with address_point as (
                    select geom from addresses where id = @addressId
                )
                select 'heritage' as source, h.id as code, h.name, h.type as category,
                       st_intersects(h.geom, a.geom) as intersects,
                       st_distance(h.geom::geography, a.geom::geography) as distance_meters
                from heritage_sites h, address_point a
                where st_dwithin(h.geom::geography, a.geom::geography, 150)
                union all
                select 'nature' as source, n.id as code, n.name, n.category,
                       st_intersects(n.geom, a.geom) as intersects,
                       null::double precision as distance_meters
                from nature_areas n, address_point a
                where st_intersects(n.geom, a.geom)
                order by source, distance_meters nulls last;
                """;

            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("addressId", address.Id);

            var findings = new List<DataFinding>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                findings.Add(new DataFinding(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetBoolean(4),
                    reader.IsDBNull(5) ? null : reader.GetDouble(5)));
            }

            return findings;
        }

        public async Task<Guid> SavePrecheckRunAsync(
            PrecheckRequest request,
            AddressRecord? address,
            PrecheckDecision decision,
            CancellationToken cancellationToken)
        {
            var requestId = Guid.NewGuid();
            var response = PrecheckResponse.FromDecision(requestId, decision);

            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            const string sql = """
                insert into precheck_runs
                    (request_id, intent_id, address_id, address_label, result_level, reason_codes, result_json, created_at)
                values
                    (@requestId, @intentId, @addressId, @addressLabel, @resultLevel, @reasonCodes::jsonb, @resultJson::jsonb, now());
                """;

            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("requestId", requestId);
            command.Parameters.AddWithValue("intentId", request.IntentId);
            command.Parameters.AddWithValue("addressId", (object?)address?.Id ?? DBNull.Value);
            command.Parameters.AddWithValue("addressLabel", (object?)address?.NormalizedAddress ?? "Adrese nav atrasta");
            command.Parameters.AddWithValue("resultLevel", decision.Level);
            command.Parameters.AddWithValue("reasonCodes", JsonSerializer.Serialize(decision.ReasonCodes, JsonOptions));
            command.Parameters.AddWithValue("resultJson", JsonSerializer.Serialize(response, JsonOptions));

            await command.ExecuteNonQueryAsync(cancellationToken);
            return requestId;
        }

        public async Task<PrecheckSummaryResponse?> GetSummaryAsync(Guid requestId, CancellationToken cancellationToken)
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            const string sql = """
                select intent_id, address_label, result_json, created_at
                from precheck_runs
                where request_id = @requestId;
                """;

            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("requestId", requestId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            var intentId = reader.GetString(0);
            var addressLabel = reader.GetString(1);
            var response = JsonSerializer.Deserialize<PrecheckResponse>(reader.GetString(2), JsonOptions);
            var createdAt = reader.GetFieldValue<DateTimeOffset>(3);

            if (response is null)
            {
                return null;
            }

            return new PrecheckSummaryResponse(
                response.RequestId,
                intentId,
                addressLabel,
                response.Level,
                response.ReasonCodes,
                response.DataFindings,
                response.NextSteps,
                response.OfficialDisclaimer,
                createdAt);
        }
    }
}
