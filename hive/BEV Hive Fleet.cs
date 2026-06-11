// ============================================================
// FILE        : BEV Hive Fleet.cs
// STATUS      : Phase 1e — FleetView backend
// PURPOSE     : Two endpoints backing the NEXUS FleetView panel.
//                 POST /v1/fleet/live  - Gateway forwards each EAGLE
//                   BevLiveSnapshot here (~per box instance). Upserted
//                   into audit.fleet_live keyed by (mid, instance_id),
//                   latest-wins. Bearer cube auth; MID normalized to C-.
//                 GET  /v1/fleet       - NEXUS reads the consolidated
//                   roster: every box (C- MID) with its live instances
//                   (state, position, pnl, trace_mode, families, regime)
//                   MERGED with an audit roll-up (TCA count + last row)
//                   from Postgres. One call, whole fleet.
//
// Live state half comes from the Gateway/EAGLE LiveLink file bridge;
// audit roll-up half comes from the already-ingested audit tables.
// Both keyed by canonical C- MID.
// ============================================================

using System.Net;
using System.Text.Json;
using BEV.Hive.Models;
using BEV.Hive.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace BEV.Hive.Functions;

public sealed class FleetFunction
{
    private readonly IJwtValidator _jwt;
    private readonly IAuditStore _audit;
    private readonly ISignalRService _sr;
    private readonly ILogger<FleetFunction> _log;

    public FleetFunction(IJwtValidator jwt, IAuditStore audit, ISignalRService sr, ILogger<FleetFunction> log)
    {
        _jwt = jwt; _audit = audit; _sr = sr; _log = log;
    }

    private static object Frame(string panelId, object payload) =>
        new { panel_id = panelId, payload, ts_utc = DateTime.UtcNow.ToString("o") };
    private static object RowFrame(string panelId, string rowKey, object? payload) =>
        new { panel_id = panelId, row_key = rowKey, payload, ts_utc = DateTime.UtcNow.ToString("o") };

    private async Task<ValidatedClaims?> AuthAsync(HttpRequest req, CancellationToken ct)
    {
        if (!req.Headers.TryGetValue("Authorization", out StringValues authHdr)) return null;
        var auth = authHdr.ToString();
        if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var claims = await _jwt.ValidateAsync(auth.Substring(7).Trim(), ct);
        return claims.Valid ? claims : null;
    }

    // ---------- POST /v1/fleet/live ----------
    [Function("FleetLiveIngest")]
    public async Task<IActionResult> Live(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "fleet/live")] HttpRequest req,
        CancellationToken ct)
    {
        var claims = await AuthAsync(req, ct);
        if (claims is null) return new UnauthorizedObjectResult(new ErrorResponse { Error = "AUTH_FAILED" });

        string body;
        using (var sr = new StreamReader(req.Body)) body = await sr.ReadToEndAsync(ct);
        if (string.IsNullOrWhiteSpace(body))
            return new BadRequestObjectResult(new ErrorResponse { Error = "EMPTY_BODY" });

        // resolve MID: X-MID header -> JWT -> payload, normalized to C-
        req.Headers.TryGetValue("X-MID", out StringValues xmid);
        string? rawMid =
            !string.IsNullOrWhiteSpace(xmid.ToString()) ? xmid.ToString() :
            !string.IsNullOrWhiteSpace(claims.MachineId) ? claims.MachineId : null;
        var mid = NormalizeMid(rawMid);

        // pull instance_id out of the JSON for the upsert key
        string instanceId;
        try
        {
            using var doc = JsonDocument.Parse(body);
            instanceId = doc.RootElement.TryGetProperty("instance_id", out var iidEl)
                ? (iidEl.GetString() ?? "") : "";
            if (mid is null && doc.RootElement.TryGetProperty("mid", out var midEl))
                mid = NormalizeMid(midEl.GetString());
        }
        catch { return new BadRequestObjectResult(new ErrorResponse { Error = "BAD_JSON" }); }

        if (string.IsNullOrWhiteSpace(mid))
            return new BadRequestObjectResult(new ErrorResponse { Error = "NO_MID" });

        await _audit.UpsertFleetLiveAsync(mid!, instanceId, body, ct);

        // push onto the live rails (tenant-scoped). fleet.roster as a row
        // delta keyed by mid:instance; header.assimilated as a snapshot.
        if (_sr.Configured)
        {
            var tenantGroup = RealtimeFunctions.TenantGroup(claims.TenantId);
            try
            {
                using var doc = JsonDocument.Parse(body);
                await _sr.SendToGroupAsync(RealtimeFunctions.HubName, tenantGroup,
                    RowFrame("fleet.roster", $"{mid}:{instanceId}", doc.RootElement.Clone()), ct);
            }
            catch { /* body already validated above; ignore push-shape errors */ }

            var total = await _audit.GetAssimilatedTradesAsync(ct);
            await _sr.SendToGroupAsync(RealtimeFunctions.HubName, tenantGroup,
                Frame("header.assimilated", new { count = total }), ct);

            // fleet.aggregate: the single cross-cube source of truth. Built
            // Hive-side (no cube sees the fleet) and pushed to the tenant
            // group; web + NT8 fleet view consume the same frame so they
            // cannot disagree. LIVE-only headline, SIM as a split line.
            try
            {
                var liveRows = await _audit.GetFleetLiveRawAsync(ct);
                var aggregate = FleetAggregator.Build(liveRows);
                await _sr.SendToGroupAsync(RealtimeFunctions.HubName, tenantGroup,
                    Frame("fleet.aggregate", aggregate), ct);
            }
            catch { /* aggregation is best-effort; never block ingest */ }
        }

        return new OkObjectResult(new { ok = true, mid, instance_id = instanceId });
    }

    // ---------- GET /v1/fleet ----------
    [Function("FleetRoster")]
    public async Task<IActionResult> Roster(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "fleet")] HttpRequest req,
        CancellationToken ct)
    {
        var claims = await AuthAsync(req, ct);
        if (claims is null) return new UnauthorizedObjectResult(new ErrorResponse { Error = "AUTH_FAILED" });

        var roster = await _audit.GetFleetRosterAsync(ct);
        return new OkObjectResult(new { ok = true, generated_utc = DateTime.UtcNow.ToString("o"), boxes = roster });
    }

    // ---------- GET /v1/tenant/pnl ----------
    // Tenant-wide P&L aggregate for fleet.pnl_total. Sums realized P&L
    // across the tenant's TCA rows, grouped by Eastern session_date.
    [Function("TenantPnl")]
    public async Task<IActionResult> TenantPnl(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "tenant/pnl")] HttpRequest req,
        CancellationToken ct)
    {
        var claims = await AuthAsync(req, ct);
        if (claims is null) return new UnauthorizedObjectResult(new ErrorResponse { Error = "AUTH_FAILED" });
        var pnl = await _audit.GetTenantPnlAsync(ct);
        return new OkObjectResult(new { ok = true, day_total = pnl.dayTotal, accounts = pnl.accounts,
                                        session_date_et = pnl.sessionDate });
    }

    // ---------- GET /v1/assimilated ----------
    // Global trade count across the ENTIRE platform — every TCA row in
    // the Hive, fleet-wide. This is the same number Phoenix/Dragon reason
    // against, so the tray + terminal show exactly what PHX/DRG see.
    [Function("AssimilatedCount")]
    public async Task<IActionResult> Assimilated(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "assimilated")] HttpRequest req,
        CancellationToken ct)
    {
        var claims = await AuthAsync(req, ct);
        if (claims is null) return new UnauthorizedObjectResult(new ErrorResponse { Error = "AUTH_FAILED" });

        var total = await _audit.GetAssimilatedTradesAsync(ct);
        return new OkObjectResult(new { ok = true, trades_assimilated = total });
    }

    private static string? NormalizeMid(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim().ToUpperInvariant();
        while (s.StartsWith("C-")) s = s.Substring(2);
        return s.Length == 0 ? null : "C-" + s;
    }
}
