// ============================================================
// FILE        : BEV Hive HUD Snapshot.cs
// STATUS      : Phase 1c-1 — Hive delta for Gateway lifecycle
// LAST UPD    : 2026-05-27 14:00 CST
// PURPOSE     : POST /v1/hud-snapshot. Gateway pushes a HUD
//               telemetry payload every 5-30s. Hive validates
//               the JWT + MID match, persists the snapshot to
//               Cosmos `telemetry` container (24h TTL), returns
//               cadence hint + rulebook version stub.
//               Sprint 1: returns canned rulebook_version. Real
//               rulebook emission lands in Phase 3.
// OWNS        : HUD telemetry ingestion.
// CALLED BY   : Gateway Windows service heartbeat.
// CHANGE LOG  :
//   2026-05-27 14:00 CST  v0-26.0527-A  Initial scaffold (Phase 1c-1).
// ============================================================

using BEV.Hive.Models;
using BEV.Hive.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using System.Text.Json;

namespace BEV.Hive.Functions;

public sealed class HudSnapshotFunction
{
    private readonly IJwtValidator _jwt;
    private readonly IHiveStorage _storage;
    private readonly ILogger<HudSnapshotFunction> _log;

    public HudSnapshotFunction(IJwtValidator jwt, IHiveStorage storage, ILogger<HudSnapshotFunction> log)
    {
        _jwt = jwt;
        _storage = storage;
        _log = log;
    }

    [Function("HudSnapshot")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "hud-snapshot")] HttpRequest req,
        CancellationToken ct)
    {
        // ---- 1. Bearer ----
        if (!req.Headers.TryGetValue("Authorization", out StringValues authHdr))
            return new UnauthorizedObjectResult(new ErrorResponse { Error = "MISSING_BEARER" });

        var raw = authHdr.ToString();
        const string prefix = "Bearer ";
        if (!raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return new UnauthorizedObjectResult(new ErrorResponse { Error = "MISSING_BEARER" });

        var token = raw.Substring(prefix.Length).Trim();
        var claims = await _jwt.ValidateAsync(token, ct);
        if (!claims.Valid)
            return new UnauthorizedObjectResult(new ErrorResponse { Error = "AUTH_FAILED", Message = claims.Error });

        // ---- 2. MID resolution ----
        // The JWT is signed and validated above, so claims.MachineId (the
        // token's `sub`) is the AUTHORITATIVE machine id. The X-MID header
        // and body.Mid are advisory only — they can legitimately lag the
        // token by one cycle right after a reprovision (Server may mint a
        // fresh MID before the Gateway updates the values it sends). We do
        // NOT 401 on a mismatch; doing so caused a green/yellow flap where
        // commands (Bearer-only) passed but hud-snapshot rejected. We log
        // the divergence for visibility and persist under claims.MachineId.
        var midHdr = req.Headers["X-MID"].ToString();
        if (!string.IsNullOrEmpty(midHdr) && midHdr != claims.MachineId)
            _log.LogInformation("HudSnapshot: X-MID header {Hdr} differs from token sub {Sub}; using token sub",
                midHdr, claims.MachineId);

        // ---- 3. Parse body ----
        HudSnapshotRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<HudSnapshotRequest>(req.Body, cancellationToken: ct);
        }
        catch (JsonException)
        {
            return new BadRequestObjectResult(new ErrorResponse { Error = "MALFORMED_REQUEST" });
        }

        if (body is null)
            return new BadRequestObjectResult(new ErrorResponse { Error = "MALFORMED_REQUEST" });

        if (!string.IsNullOrEmpty(body.Mid) && body.Mid != claims.MachineId)
            _log.LogInformation("HudSnapshot: body.Mid {Body} differs from token sub {Sub}; using token sub",
                body.Mid, claims.MachineId);

        // ---- 4. Persist ----
        var nowUtc = DateTime.UtcNow.ToString("o");
        var doc = new HudSnapshotDoc
        {
            // One row per Cube per minute. Using minute granularity
            // means rapid Gateway updates within the same minute
            // overwrite each other — fine for telemetry, lower
            // storage churn. Real fleet-wide history lands in
            // Postgres in Phase 2.
            Id          = $"{claims.MachineId}-{DateTime.UtcNow:yyyyMMddHHmm}",
            TenantId    = claims.TenantId,
            MachineId   = claims.MachineId,
            ReceivedUtc = nowUtc,
            WallUtc     = body.WallUtc,
            BuildLabel  = body.BuildLabel,
            Payload     = body
        };

        await _storage.UpsertHudSnapshotAsync(doc, ct);

        _log.LogInformation("HUD snapshot: tenant={Tenant} mid={Mid} build={Build} nt8={Nt8} nexus={Nx} l2={L2}",
            claims.TenantId, claims.MachineId, body.BuildLabel, body.Nt8State, body.NexusLoaded,
            body.Feeds?.L2);

        return new OkObjectResult(new HudSnapshotResponse
        {
            ReceivedUtc     = nowUtc,
            NextPollSec     = 15,
            RulebookVersion = "rb-stub-1"
        });
    }
}
