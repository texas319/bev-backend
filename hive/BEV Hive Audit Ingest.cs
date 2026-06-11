// ============================================================
// FILE        : BEV Hive Audit Ingest.cs
// STATUS      : Phase 2 — audit pipeline (item 11)
// LAST UPD    : 2026-06-02 17:30 EST
// PURPOSE     : POST /v1/audit/ingest. Accepts one EAGLE audit
//               CSV (filename + raw text), classifies it into one
//               of EIGHT log types, parses + type-coerces rows,
//               and writes to the matching audit.* Postgres table.
//               Idempotent via content SHA in audit.ingest_ledger.
//               Tenant + MID come from the validated JWT (sub/tid),
//               same authoritative model as hud-snapshot.
// OWNS        : Audit row ingestion into Postgres (the AI/analytics
//               source of truth). Does NOT touch the relay
//               endpoints (hud-snapshot/commands) — additive only.
// CALLED BY   : Gateway audit shipper (tails audit CSVs, posts each).
// QUIRKS HANDLED (locked w/ Risk 2026-06-02):
//   * orphan trades (OL/barsnap/sigeval w/o tca) — tables independent
//   * exit_name NULL — nullable
//   * close_reason bare-or-granular — free text
//   * build_version legacy v1-26.* OR BEV.0602.26-AA — free text
//   * trace/diag PascalCase + "MM-DD-YY HH:MM:SS" — normalized,
//     raw_payload preserves originals; repeated headers + blank
//     separator rows skipped
//   * sigeval.gate_details / order_lifecycle.notes — JSONB passthrough
//   * perf/settings vertical EAV — row-per-kv
// CHANGE LOG  :
//   2026-06-02 17:30 EST  HV.0602.26-B  Audit ingest endpoint + 8-table
//                          router. Validated against live bundle
//                          BEV-EAGLE-06-02-26 (163 CSV, 0 failures).
// ============================================================

using BEV.Hive.Models;
using BEV.Hive.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BEV.Hive.Functions;

public sealed class AuditIngestFunction
{
    private readonly IJwtValidator _jwt;
    private readonly IAuditStore _audit;
    private readonly ILogger<AuditIngestFunction> _log;

    public AuditIngestFunction(IJwtValidator jwt, IAuditStore audit, ILogger<AuditIngestFunction> log)
    {
        _jwt = jwt;
        _audit = audit;
        _log = log;
    }

    [Function("AuditIngest")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "audit/ingest")] HttpRequest req,
        CancellationToken ct)
    {
        // ---- 1. Bearer (same authoritative model as hud-snapshot) ----
        if (!req.Headers.TryGetValue("Authorization", out StringValues authHdr))
            return new UnauthorizedObjectResult(new ErrorResponse { Error = "MISSING_BEARER" });
        var raw = authHdr.ToString();
        const string prefix = "Bearer ";
        if (!raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return new UnauthorizedObjectResult(new ErrorResponse { Error = "MISSING_BEARER" });

        var claims = await _jwt.ValidateAsync(raw.Substring(prefix.Length).Trim(), ct);
        if (!claims.Valid)
            return new UnauthorizedObjectResult(new ErrorResponse { Error = "AUTH_FAILED", Message = claims.Error });

        // ---- 2. Parse body: { file_name, content } ----
        AuditIngestRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<AuditIngestRequest>(req.Body, cancellationToken: ct);
        }
        catch (JsonException)
        {
            return new BadRequestObjectResult(new ErrorResponse { Error = "MALFORMED_REQUEST" });
        }
        if (body is null || string.IsNullOrWhiteSpace(body.FileName) || body.Content is null)
            return new BadRequestObjectResult(new ErrorResponse { Error = "MALFORMED_REQUEST" });

        // ---- 3. Classify ----
        var logType = AuditRouter.Classify(body.FileName);
        if (logType is null)
            return new BadRequestObjectResult(new ErrorResponse { Error = "UNCLASSIFIED_FILE", Message = body.FileName });

        // Canonical MID for the ledger. Resolution order: JWT MachineId
        // claim -> X-MID header (the Gateway always sends this) -> MID
        // encoded in the file name. Normalize to platform-canonical
        // "C-XXXXXX" so the ledger is uniform regardless of source.
        req.Headers.TryGetValue("X-MID", out StringValues xmidHdr);
        var rawMid =
            !string.IsNullOrWhiteSpace(claims.MachineId) ? claims.MachineId :
            !string.IsNullOrWhiteSpace(xmidHdr.ToString()) ? xmidHdr.ToString() :
            AuditRouter.MidFromNamePublic(body.FileName);
        var ledgerMid = AuditRouter.NormalizeMid(rawMid);

        // ---- 4. Idempotency: skip if this exact content already ingested ----
        var sha = Sha256(body.Content);
        if (await _audit.AlreadyIngestedAsync(sha, ct))
        {
            _log.LogInformation("AuditIngest: duplicate {File} ({Type}) sha={Sha} — skipped",
                body.FileName, logType, sha[..12]);
            return new OkObjectResult(new AuditIngestResponse
            {
                Status = "DUPLICATE", LogType = logType, RowsInserted = 0
            });
        }

        // ---- 5. Parse + coerce rows (logic mirrors validated router) ----
        AuditParseResult parsed;
        try
        {
            parsed = AuditRouter.BuildRows(logType, body.Content, body.FileName);
        }
        catch (Exception ex)
        {
            // Record the failure for visibility, but DO NOT store the content
            // SHA — otherwise the dedup guard would treat this exact content as
            // "already seen" and block a corrected build from re-ingesting it.
            // (A null sha is not deduped: multiple NULLs are distinct in the
            // unique index.) A successful re-POST later records the real sha.
            await _audit.RecordLedgerAsync(body.FileName, logType, ledgerMid,
                0, 0, "FAILED", ex.Message, null, ct);
            _log.LogError(ex, "AuditIngest: parse failed for {File} ({Type})", body.FileName, logType);
            return new ObjectResult(new ErrorResponse { Error = "PARSE_FAILED", Message = ex.Message })
            { StatusCode = StatusCodes.Status500InternalServerError };
        }

        // ---- 6. Write to the matching table (orphan-tolerant; no joins) ----
        int inserted = await _audit.InsertRowsAsync(logType, parsed.Rows, ct);

        await _audit.RecordLedgerAsync(body.FileName, logType, ledgerMid,
            parsed.Rows.Count, inserted, "OK", null, sha, ct);

        _log.LogInformation("AuditIngest: tenant={Tenant} mid={Mid} file={File} type={Type} parsed={P} inserted={I}",
            claims.TenantId, claims.MachineId, body.FileName, logType, parsed.Rows.Count, inserted);

        return new OkObjectResult(new AuditIngestResponse
        {
            Status = "OK",
            LogType = logType,
            RowsParsed = parsed.Rows.Count,
            RowsInserted = inserted
        });
    }

    private static string Sha256(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
