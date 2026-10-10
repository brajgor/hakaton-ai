using Npgsql;
using VaiDrikstu.Domain;

namespace VaiDrikstu.Api;

public static class RuleRegistrySeeder
{
    public static async Task SeedAsync(NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        const string schema = """
            create extension if not exists postgis;
            create table if not exists legal_sources (
                source_id text primary key,
                title text not null,
                official_url text not null
            );
            create table if not exists legal_source_versions (
                source_id text not null references legal_sources(source_id),
                snapshot_sha256 text not null,
                retrieved_at date not null,
                primary key (source_id, snapshot_sha256)
            );
            create table if not exists rules (
                rule_id text not null,
                version integer not null,
                effective_from date not null,
                effective_to date null,
                jurisdiction text not null,
                primary key (rule_id, version)
            );
            create table if not exists rule_conditions (
                rule_id text not null,
                rule_version integer not null,
                intent_id text not null,
                spatial_flag text not null,
                requires_intersection boolean not null,
                primary key (rule_id, rule_version, intent_id, spatial_flag),
                foreign key (rule_id, rule_version) references rules(rule_id, version)
            );
            create table if not exists rule_effects (
                rule_id text not null,
                rule_version integer not null,
                status text not null,
                reason_code text not null,
                next_steps jsonb not null,
                primary key (rule_id, rule_version),
                foreign key (rule_id, rule_version) references rules(rule_id, version)
            );
            create table if not exists rule_citations (
                rule_id text not null,
                rule_version integer not null,
                source_id text not null,
                section text not null,
                source_snapshot_sha256 text not null,
                primary key (rule_id, rule_version, source_id, section),
                foreign key (rule_id, rule_version) references rules(rule_id, version),
                foreign key (source_id) references legal_sources(source_id)
            );
            create table if not exists spatial_flags (
                id text primary key,
                flag_type text not null,
                name text not null,
                category text not null,
                source_id text not null,
                geom geometry(Geometry, 4326) not null
            );
            create index if not exists ix_spatial_flags_geom on spatial_flags using gist (geom);
            alter table precheck_runs add column if not exists regulatory_status text not null default 'no_known_blocker';
            alter table precheck_runs add column if not exists coverage_warnings jsonb not null default '[]'::jsonb;
            alter table precheck_runs add column if not exists applied_rules jsonb not null default '[]'::jsonb;
            alter table precheck_runs add column if not exists project_parameters jsonb not null default '{}'::jsonb;
            alter table precheck_runs add column if not exists checked_on date not null default current_date;
            """;

        await ExecuteAsync(connection, transaction, schema);

        foreach (var source in RuleCatalog.SourceSnapshots)
        {
            await ExecuteAsync(connection, transaction, """
                insert into legal_sources (source_id, title, official_url)
                values (@sourceId, @title, @url)
                on conflict (source_id) do update set title = excluded.title, official_url = excluded.official_url;
                insert into legal_source_versions (source_id, snapshot_sha256, retrieved_at)
                values (@sourceId, @hash, date '2026-10-10')
                on conflict do nothing;
            """, command =>
            {
                command.Parameters.AddWithValue("sourceId", source.SourceId);
                command.Parameters.AddWithValue("title", source.Title);
                command.Parameters.AddWithValue("url", source.OfficialUrl);
                command.Parameters.AddWithValue("hash", source.SnapshotSha256);
            });
        }

        foreach (var rule in RuleCatalog.All)
        {
            await ExecuteAsync(connection, transaction, """
                insert into rules (rule_id, version, effective_from, effective_to, jurisdiction)
                values (@id, @version, @effectiveFrom, @effectiveTo, @jurisdiction)
                on conflict (rule_id, version) do update set
                    effective_from = excluded.effective_from,
                    effective_to = excluded.effective_to,
                    jurisdiction = excluded.jurisdiction;
                insert into rule_effects (rule_id, rule_version, status, reason_code, next_steps)
                values (@id, @version, @status, @reasonCode, @nextSteps::jsonb)
                on conflict (rule_id, rule_version) do update set
                    status = excluded.status,
                    reason_code = excluded.reason_code,
                    next_steps = excluded.next_steps;
                """, command =>
            {
                command.Parameters.AddWithValue("id", rule.RuleId);
                command.Parameters.AddWithValue("version", rule.Version);
                command.Parameters.AddWithValue("effectiveFrom", rule.EffectiveFrom.ToDateTime(TimeOnly.MinValue));
                command.Parameters.AddWithValue("effectiveTo", (object?)rule.EffectiveTo?.ToDateTime(TimeOnly.MinValue) ?? DBNull.Value);
                command.Parameters.AddWithValue("jurisdiction", rule.Jurisdiction);
                command.Parameters.AddWithValue("status", rule.Status);
                command.Parameters.AddWithValue("reasonCode", rule.ReasonCode);
                command.Parameters.AddWithValue("nextSteps", System.Text.Json.JsonSerializer.Serialize(rule.NextSteps));
            });

            foreach (var intent in rule.IntentAny)
            foreach (var spatialFlag in rule.SpatialFlagsAny)
            {
                await ExecuteAsync(connection, transaction, """
                    insert into rule_conditions (rule_id, rule_version, intent_id, spatial_flag, requires_intersection)
                    values (@id, @version, @intent, @flag, @requiresIntersection)
                    on conflict (rule_id, rule_version, intent_id, spatial_flag) do update set
                        requires_intersection = excluded.requires_intersection;
                    """, command =>
                {
                    command.Parameters.AddWithValue("id", rule.RuleId);
                    command.Parameters.AddWithValue("version", rule.Version);
                    command.Parameters.AddWithValue("intent", intent);
                    command.Parameters.AddWithValue("flag", spatialFlag);
                    command.Parameters.AddWithValue("requiresIntersection", rule.RequiresIntersection);
                });
            }

            foreach (var citation in rule.Citations)
            {
                await ExecuteAsync(connection, transaction, """
                    insert into rule_citations (rule_id, rule_version, source_id, section, source_snapshot_sha256)
                    values (@id, @version, @sourceId, @section, @hash)
                    on conflict do nothing;
                    """, command =>
                {
                    command.Parameters.AddWithValue("id", rule.RuleId);
                    command.Parameters.AddWithValue("version", rule.Version);
                    command.Parameters.AddWithValue("sourceId", citation.SourceId);
                    command.Parameters.AddWithValue("section", citation.Section);
                    command.Parameters.AddWithValue("hash", citation.SourceSnapshotSha256);
                });
            }
        }

        await transaction.CommitAsync();
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        Action<NpgsqlCommand>? configure = null)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        configure?.Invoke(command);
        await command.ExecuteNonQueryAsync();
    }
}
