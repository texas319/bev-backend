// ============================================================
// FILE        : BEV Hive Audit Store.cs
// STATUS      : Phase 2 — audit pipeline (item 11)
// PURPOSE     : Postgres persistence for the 8 audit tables. Writes
//               parsed rows via parameterized INSERTs built from the
//               row dictionaries (column set is data-driven, so a
//               schema column added in EAGLE just flows through once
//               the table has the column). Idempotency + ledger.
// NOTES       :
//   * Connection string from env AUDIT_PG_CONN (Key Vault ref in prod).
//   * barsnap daily partitions are created on demand (CREATE TABLE IF
//     NOT EXISTS ... PARTITION OF) keyed by session_date.
//   * JSONB columns (gate_details, notes, raw_payload) are passed as
//     NpgsqlDbType.Jsonb with serialized JSON text.
//   * Unknown dictionary keys (not real columns) are dropped at insert
//     against the table's known column list — defensive against EAGLE
//     emitting a field before the migration adds it.
// ============================================================

using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace BEV.Hive.Services;

public interface IAuditStore
{
    Task<bool> AlreadyIngestedAsync(string sha256, CancellationToken ct);
    Task<int>  InsertRowsAsync(string logType, List<Dictionary<string, object?>> rows, CancellationToken ct);
    Task RecordLedgerAsync(string fileName, string logType, string? mid,
        int parsed, int inserted, string status, string? detail, string? sha256, CancellationToken ct);

    // FleetView: upsert the latest live snapshot per (mid, instance_id);
    // read the consolidated roster (live state + audit roll-up per MID).
    Task UpsertFleetLiveAsync(string mid, string instanceId, string snapshotJson, CancellationToken ct);
    Task<List<Dictionary<string, object?>>> GetFleetRosterAsync(CancellationToken ct);
    Task<List<(string mid, string snapshotJson)>> GetFleetLiveRawAsync(CancellationToken ct);
    Task<long> GetAssimilatedTradesAsync(CancellationToken ct);
    Task<(decimal dayTotal, int accounts, string sessionDate)> GetTenantPnlAsync(CancellationToken ct);
}

public sealed class PostgresAuditStore : IAuditStore
{
    private readonly string _conn;
    private static readonly HashSet<string> JsonbCols = new() { "gate_details", "notes", "raw_payload" };

    // cache of known columns per table (loaded from information_schema once)
    private readonly Dictionary<string, HashSet<string>> _cols = new();
    private readonly SemaphoreSlim _colLock = new(1, 1);
    private readonly HashSet<string> _ensuredPartitions = new();

    public PostgresAuditStore(string connectionString) => _conn = connectionString;

    public async Task<bool> AlreadyIngestedAsync(string sha256, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(_conn);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT 1 FROM audit.ingest_ledger WHERE content_sha256 = @s LIMIT 1", c);
        cmd.Parameters.AddWithValue("s", sha256);
        var r = await cmd.ExecuteScalarAsync(ct);
        return r != null;
    }

    public async Task<int> InsertRowsAsync(string logType, List<Dictionary<string, object?>> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return 0;
        await using var c = new NpgsqlConnection(_conn);
        await c.OpenAsync(ct);

        var known = await GetColumnsAsync(c, logType, ct);
        int inserted = 0;

        await using var tx = await c.BeginTransactionAsync(ct);
        foreach (var row in rows)
        {
            // barsnap: ensure the daily partition exists before inserting
            if (logType == "barsnap" && row.TryGetValue("session_date", out var sd) && sd is DateTime dts)
                await EnsureBarsnapPartitionAsync(c, tx, DateOnly.FromDateTime(dts), ct);

            var usable = row.Keys.Where(k => known.Contains(k) && row[k] != null).ToList();
            if (usable.Count == 0) continue;

            var sb = new StringBuilder($"INSERT INTO audit.{logType} (");
            sb.Append(string.Join(",", usable));
            sb.Append(") VALUES (");
            sb.Append(string.Join(",", usable.Select((_, idx) => "@p" + idx)));
            sb.Append(')');

            await using var cmd = new NpgsqlCommand(sb.ToString(), c, (NpgsqlTransaction)tx);
            for (int idx = 0; idx < usable.Count; idx++)
            {
                var key = usable[idx]; var val = row[key];
                var p = new NpgsqlParameter("p" + idx, ToDb(key, val));
                if (JsonbCols.Contains(key)) p.NpgsqlDbType = NpgsqlDbType.Jsonb;
                cmd.Parameters.Add(p);
            }
            inserted += await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return inserted;
    }

    public async Task RecordLedgerAsync(string fileName, string logType, string? mid,
        int parsed, int inserted, string status, string? detail, string? sha256, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(_conn);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO audit.ingest_ledger
              (file_name, log_type, mid, rows_parsed, rows_inserted, status, detail, content_sha256)
            VALUES (@f,@t,@m,@rp,@ri,@s,@d,@sha)
            ON CONFLICT (content_sha256) DO NOTHING", c);
        cmd.Parameters.AddWithValue("f", fileName);
        cmd.Parameters.AddWithValue("t", logType);
        cmd.Parameters.AddWithValue("m", (object?)mid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("rp", parsed);
        cmd.Parameters.AddWithValue("ri", inserted);
        cmd.Parameters.AddWithValue("s", status);
        cmd.Parameters.AddWithValue("d", (object?)detail ?? DBNull.Value);
        cmd.Parameters.AddWithValue("sha", (object?)sha256 ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertFleetLiveAsync(string mid, string instanceId, string snapshotJson, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(_conn);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO audit.fleet_live (mid, instance_id, snapshot, updated_utc)
            VALUES (@m,@i,@s::jsonb, now())
            ON CONFLICT (mid, instance_id)
            DO UPDATE SET snapshot = EXCLUDED.snapshot, updated_utc = now()", c);
        cmd.Parameters.AddWithValue("m", mid);
        cmd.Parameters.AddWithValue("i", instanceId ?? "");
        cmd.Parameters.AddWithValue("s", snapshotJson);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<(string mid, string snapshotJson)>> GetFleetLiveRawAsync(CancellationToken ct)
    {
        // Raw (mid, snapshot) for every live instance. Feeds FleetAggregator
        // for the cross-cube fleet.aggregate frame. Un-scoped to match
        // GetFleetRosterAsync + single-tenant reality; pushed to the
        // ingesting tenant's group by the caller.
        var rows = new List<(string, string)>();
        await using var c = new NpgsqlConnection(_conn);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT mid, snapshot::text FROM audit.fleet_live", c);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var mid  = await r.IsDBNullAsync(0, ct) ? "" : r.GetString(0);
            var snap = await r.IsDBNullAsync(1, ct) ? "" : r.GetString(1);
            if (mid.Length > 0 && snap.Length > 0) rows.Add((mid, snap));
        }
        return rows;
    }

    public async Task<List<Dictionary<string, object?>>> GetFleetRosterAsync(CancellationToken ct)
    {
        // One row per box (mid): its live instances (jsonb array) merged
        // with an audit roll-up (tca count + last row time). LEFT JOINs so
        // a box that's live-but-no-audit or audit-but-not-live still shows.
        const string sql = @"
            WITH live AS (
                SELECT mid,
                       jsonb_agg(snapshot ORDER BY instance_id) AS instances,
                       max(updated_utc) AS last_live_utc,
                       count(*) AS instance_count
                FROM audit.fleet_live
                GROUP BY mid
            ),
            tca_roll AS (
                SELECT mid, count(*) AS tca_rows, max(session_date) AS last_session
                FROM audit.tca GROUP BY mid
            ),
            ship AS (
                SELECT mid, count(*) AS files_shipped, max(ingested_at) AS last_ship_utc
                FROM audit.ingest_ledger WHERE mid IS NOT NULL AND mid <> 'C-' GROUP BY mid
            )
            SELECT COALESCE(live.mid, tca_roll.mid, ship.mid) AS mid,
                   live.instances, live.instance_count,
                   (live.last_live_utc AT TIME ZONE 'America/New_York') AS last_live_et,
                   tca_roll.tca_rows, tca_roll.last_session,
                   ship.files_shipped,
                   (ship.last_ship_utc AT TIME ZONE 'America/New_York') AS last_ship_et
            FROM live
            FULL OUTER JOIN tca_roll ON tca_roll.mid = live.mid
            FULL OUTER JOIN ship     ON ship.mid     = COALESCE(live.mid, tca_roll.mid)
            ORDER BY 1";
        var rows = new List<Dictionary<string, object?>>();
        await using var c = new NpgsqlConnection(_conn);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var d = new Dictionary<string, object?>();
            for (int i = 0; i < r.FieldCount; i++)
                d[r.GetName(i)] = await r.IsDBNullAsync(i, ct) ? null : r.GetValue(i);
            rows.Add(d);
        }
        return rows;
    }

    public async Task<(decimal dayTotal, int accounts, string sessionDate)> GetTenantPnlAsync(CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(_conn);
        await c.OpenAsync(ct);
        const string sql = @"
            WITH latest AS (SELECT max(session_date) AS d FROM audit.tca)
            SELECT COALESCE(sum(t.net_pnl),0)::numeric AS day_total,
                   COALESCE(count(DISTINCT t.account_tier),0) AS accounts,
                   to_char((SELECT d FROM latest),'YYYY-MM-DD') AS sd
            FROM audit.tca t WHERE t.session_date = (SELECT d FROM latest)";
        await using var cmd = new NpgsqlCommand(sql, c);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (await r.ReadAsync(ct))
        {
            var total = r.IsDBNull(0) ? 0m : r.GetDecimal(0);
            var accts = r.IsDBNull(1) ? 0 : Convert.ToInt32(r.GetValue(1));
            var sd = r.IsDBNull(2) ? "" : r.GetString(2);
            return (total, accts, sd);
        }
        return (0m, 0, "");
    }

    public async Task<long> GetAssimilatedTradesAsync(CancellationToken ct)
    {
        // Global trade count = every TCA row in the Hive, fleet-wide.
        await using var c = new NpgsqlConnection(_conn);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM audit.tca", c);
        var n = await cmd.ExecuteScalarAsync(ct);
        return n is long l ? l : Convert.ToInt64(n);
    }

    // ---- helpers ----
    private static object ToDb(string key, object? val)
    {
        if (val is null) return DBNull.Value;
        if (JsonbCols.Contains(key))
            return val is JsonElement je ? je.GetRawText() : JsonSerializer.Serialize(val);
        return val;
    }

    private async Task<HashSet<string>> GetColumnsAsync(NpgsqlConnection c, string table, CancellationToken ct)
    {
        if (_cols.TryGetValue(table, out var cached)) return cached;
        await _colLock.WaitAsync(ct);
        try
        {
            if (_cols.TryGetValue(table, out cached)) return cached;
            var set = new HashSet<string>(StringComparer.Ordinal);
            await using var cmd = new NpgsqlCommand(
                "SELECT column_name FROM information_schema.columns WHERE table_schema='audit' AND table_name=@t", c);
            cmd.Parameters.AddWithValue("t", table);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) set.Add(rd.GetString(0));
            _cols[table] = set;
            return set;
        }
        finally { _colLock.Release(); }
    }

    private async Task EnsureBarsnapPartitionAsync(NpgsqlConnection c, System.Data.Common.DbTransaction tx,
        DateOnly day, CancellationToken ct)
    {
        var name = $"barsnap_{day:yyyyMMdd}";
        if (_ensuredPartitions.Contains(name)) return;
        var next = day.AddDays(1);
        var sql = $@"CREATE TABLE IF NOT EXISTS audit.{name}
                     PARTITION OF audit.barsnap
                     FOR VALUES FROM ('{day:yyyy-MM-dd}') TO ('{next:yyyy-MM-dd}');";
        await using var cmd = new NpgsqlCommand(sql, c, (NpgsqlTransaction)tx);
        await cmd.ExecuteNonQueryAsync(ct);
        _ensuredPartitions.Add(name);
    }
}
