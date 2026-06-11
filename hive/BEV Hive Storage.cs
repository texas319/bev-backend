// ============================================================
// FILE        : BEV Hive Storage.cs
// STATUS      : Phase 1c-1 — Hive delta for Gateway lifecycle
// LAST UPD    : 2026-05-27 14:00 CST
// PURPOSE     : Cosmos data layer for Hive. HUD snapshots
//               (telemetry, 24h TTL), commands (cycles, 7d TTL).
//               No Postgres yet — that's Phase 2 audit pipeline.
// OWNS        : All Cosmos document I/O for Hive.
// CALLED BY   : HudSnapshotFunction, CommandsFunction,
//               CommandAckFunction.
// CHANGE LOG  :
//   2026-05-27 14:00 CST  v0-26.0527-A  Initial scaffold (Phase 1c-1).
// ============================================================

using BEV.Hive.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using System.Net;

namespace BEV.Hive.Services;

public interface IHiveStorage
{
    Task UpsertHudSnapshotAsync(HudSnapshotDoc doc, CancellationToken ct);

    Task<List<CommandDoc>> FindPendingCommandsAsync(
        string tenantId, string machineId, DateTime sinceUtc, CancellationToken ct);

    Task MarkDeliveredAsync(string commandId, string tenantId, CancellationToken ct);

    // Drop 2 — control plane: write an INVOKE command for a Cube's Gateway
    // to drain via the existing GET /v1/commands long-poll. machineId may
    // be empty for a tenant-wide/nuclear fan-out (Gateway matches its own).
    Task EnqueueCommandAsync(CommandDoc doc, CancellationToken ct);

    Task<CommandDoc?> AckCommandAsync(
        string commandId, string tenantId,
        string result, string? detail, string executedUtc,
        CancellationToken ct);
}

public sealed class HiveStorage : IHiveStorage
{
    private readonly CosmosClient _cosmos;
    private readonly ILogger<HiveStorage> _log;
    private readonly Container _telemetry;
    private readonly Container _cycles;

    public HiveStorage(CosmosClient cosmos, ILogger<HiveStorage> log)
    {
        _cosmos = cosmos;
        _log = log;

        var db = Environment.GetEnvironmentVariable("COSMOS_DATABASE") ?? "bev";
        _telemetry = _cosmos.GetContainer(db, "telemetry");
        _cycles    = _cosmos.GetContainer(db, "cycles");
    }

    public async Task UpsertHudSnapshotAsync(HudSnapshotDoc doc, CancellationToken ct)
    {
        await _telemetry.UpsertItemAsync(doc, new PartitionKey(doc.TenantId), cancellationToken: ct);
    }

    public async Task EnqueueCommandAsync(CommandDoc doc, CancellationToken ct)
    {
        // commands live in the cycles container, partitioned by tenantId,
        // drained by the Gateway's GET /v1/commands long-poll.
        await _cycles.UpsertItemAsync(doc, new PartitionKey(doc.TenantId), cancellationToken: ct);
    }

    public async Task<List<CommandDoc>> FindPendingCommandsAsync(
        string tenantId, string machineId, DateTime sinceUtc, CancellationToken ct)
    {
        // Pending = matching MID + not yet acked + not yet expired
        // + issued after the caller's since_utc cursor.
        var nowIso = DateTime.UtcNow.ToString("o");
        var sinceIso = sinceUtc.ToString("o");

        var q = new QueryDefinition(@"
            SELECT * FROM c
            WHERE c.docType = 'command'
              AND c.tenantId = @t
              AND c.machineId = @m
              AND c.ackedUtc = null
              AND c.expiresUtc > @now
              AND c.issuedUtc > @since
            ORDER BY c.issuedUtc ASC")
            .WithParameter("@t", tenantId)
            .WithParameter("@m", machineId)
            .WithParameter("@now", nowIso)
            .WithParameter("@since", sinceIso);

        var results = new List<CommandDoc>();
        using var iter = _cycles.GetItemQueryIterator<CommandDoc>(q,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        while (iter.HasMoreResults)
        {
            var page = await iter.ReadNextAsync(ct);
            results.AddRange(page);
        }
        return results;
    }

    public async Task MarkDeliveredAsync(string commandId, string tenantId, CancellationToken ct)
    {
        try
        {
            var resp = await _cycles.ReadItemAsync<CommandDoc>(
                commandId, new PartitionKey(tenantId), cancellationToken: ct);
            var doc = resp.Resource;
            if (doc.DeliveredUtc is null)
            {
                doc.DeliveredUtc = DateTime.UtcNow.ToString("o");
                await _cycles.ReplaceItemAsync(doc, doc.Id,
                    new PartitionKey(tenantId), cancellationToken: ct);
            }
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Race with TTL or external delete — log and move on.
            _log.LogWarning("MarkDelivered: command {Id} not found.", commandId);
        }
    }

    public async Task<CommandDoc?> AckCommandAsync(
        string commandId, string tenantId,
        string result, string? detail, string executedUtc,
        CancellationToken ct)
    {
        try
        {
            var resp = await _cycles.ReadItemAsync<CommandDoc>(
                commandId, new PartitionKey(tenantId), cancellationToken: ct);
            var doc = resp.Resource;

            doc.AckedUtc = DateTime.UtcNow.ToString("o");
            doc.Result   = result;
            doc.Detail   = detail;
            // Preserve executedUtc as detail metadata; the canonical
            // "when" is AckedUtc on the row.

            await _cycles.ReplaceItemAsync(doc, doc.Id,
                new PartitionKey(tenantId), cancellationToken: ct);
            return doc;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _log.LogWarning("Ack: command {Id} not found.", commandId);
            return null;
        }
    }
}
