// ============================================================
// FILE        : BEV Hive Seven Query.cs
// STATUS      : Phase 1b — Hive /v1/seven/query stub
// LAST UPD    : 2026-05-24 13:00 CST
// PURPOSE     : POST /v1/seven/query. Validates Bearer JWT,
//               validates required headers, validates request
//               body schema, returns a canned response with
//               valid usage block and source:"dev_fallback".
//               No Gemini, no Cosmos, no Collective cycle.
//               Phase 1c+ replaces the canned content with real
//               reasoning.
// OWNS        : Seven query endpoint surface.
// CALLED BY   : Gateway (WebSocket-relayed from NEXUS).
// CHANGE LOG  :
//   2026-05-24 13:00 CST  v0-26.0524-B  Initial scaffold (Phase 1b).
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

public sealed class SevenQueryFunction
{
    private readonly IJwtValidator _jwt;
    private readonly ILogger<SevenQueryFunction> _log;

    public SevenQueryFunction(IJwtValidator jwt, ILogger<SevenQueryFunction> log)
    {
        _jwt = jwt;
        _log = log;
    }

    [Function("SevenQuery")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "seven/query")] HttpRequest req,
        CancellationToken ct)
    {
        // ---- 1. Extract bearer ----
        var bearer = ExtractBearer(req);
        if (string.IsNullOrEmpty(bearer))
            return new UnauthorizedObjectResult(new ErrorResponse { Error = "MISSING_BEARER" });

        // ---- 2. Validate JWT ----
        var claims = await _jwt.ValidateAsync(bearer, ct);
        if (!claims.Valid)
        {
            _log.LogWarning("Seven query rejected: {Error}", claims.Error);
            return new UnauthorizedObjectResult(new ErrorResponse
            {
                Error = "AUTH_FAILED",
                Message = claims.Error
            });
        }

        // ---- 3. Required headers ----
        var cubeMid = req.Headers["X-Cube-MID"].ToString();
        var tenantHeader = req.Headers["X-Tenant-Id"].ToString();
        var build = req.Headers["X-Build"].ToString();
        var mode = req.Headers["X-Mode"].ToString();

        if (string.IsNullOrEmpty(cubeMid) || string.IsNullOrEmpty(tenantHeader) ||
            string.IsNullOrEmpty(mode))
        {
            return new BadRequestObjectResult(new ErrorResponse
            {
                Error = "MISSING_HEADERS",
                Message = "Required: X-Cube-MID, X-Tenant-Id, X-Mode."
            });
        }

        if (!Modes.IsValid(mode))
        {
            return new BadRequestObjectResult(new ErrorResponse
            {
                Error = "INVALID_MODE",
                Message = $"Mode '{mode}' is not Eagle/Phoenix/Dragon."
            });
        }

        // ---- 4. Cross-check token claims vs headers ----
        // Token tells us the truth; header MUST match. Stops a Gateway
        // from spoofing a different MID/tenant than its token allows.
        if (claims.MachineId != cubeMid || claims.TenantId != tenantHeader)
        {
            _log.LogWarning("Header/claim mismatch: token mid={TokenMid} hdr={HdrMid}, token tid={TokenTid} hdr={HdrTid}",
                claims.MachineId, cubeMid, claims.TenantId, tenantHeader);
            return new UnauthorizedObjectResult(new ErrorResponse
            {
                Error = "HEADER_CLAIM_MISMATCH",
                Message = "X-Cube-MID and X-Tenant-Id must match the bearer token claims."
            });
        }

        // ---- 5. Tier gate ----
        // Phoenix requires phoenix+ tier; Dragon requires dragon tier.
        // Eagle mode is open to all tiers.
        if (mode == Modes.Phoenix && claims.Tier == "eagle")
        {
            return new ObjectResult(new ErrorResponse
            {
                Error = "TIER_INSUFFICIENT",
                Message = "Phoenix mode requires phoenix or dragon tier."
            }) { StatusCode = 403 };
        }
        if (mode == Modes.Dragon && claims.Tier != "dragon")
        {
            return new ObjectResult(new ErrorResponse
            {
                Error = "TIER_INSUFFICIENT",
                Message = "Dragon mode requires dragon tier."
            }) { StatusCode = 403 };
        }

        // ---- 6. Parse body ----
        SevenQueryRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<SevenQueryRequest>(req.Body, cancellationToken: ct);
        }
        catch (JsonException)
        {
            return new BadRequestObjectResult(new ErrorResponse { Error = "MALFORMED_REQUEST" });
        }

        if (body is null || body.Prompt is null || string.IsNullOrEmpty(body.Prompt.Instruction))
        {
            return new BadRequestObjectResult(new ErrorResponse
            {
                Error = "MALFORMED_REQUEST",
                Message = "Request requires prompt.instruction."
            });
        }

        if (string.IsNullOrEmpty(body.RequestId))
            body.RequestId = Guid.NewGuid().ToString("N").Substring(0, 12);

        // ---- 7. Dragon tier sub-check ----
        if (mode == Modes.Dragon && body.DragonTier.HasValue)
        {
            if (body.DragonTier.Value < 1 || body.DragonTier.Value > 4)
            {
                return new BadRequestObjectResult(new ErrorResponse { Error = "INVALID_DRAGON_TIER" });
            }
            if (body.DragonTier.Value > claims.DragonTierMax)
            {
                return new ObjectResult(new ErrorResponse
                {
                    Error = "DRAGON_TIER_EXCEEDED",
                    Message = $"License caps dragon tier at {claims.DragonTierMax}; requested {body.DragonTier.Value}."
                }) { StatusCode = 403 };
            }
        }

        _log.LogInformation("Seven query: req={ReqId} tenant={TenantId} mid={Mid} mode={Mode} build={Build}",
            body.RequestId, claims.TenantId, claims.MachineId, mode, build);

        // ---- 8. Canned dev_fallback response (Phase 1b stub) ----
        var hiveBuild = Environment.GetEnvironmentVariable("HIVE_BUILD_LABEL") ?? "HV.unknown";
        var response = new SevenQueryResponse
        {
            RequestId    = body.RequestId,
            ResponseText = $"[dev_fallback] Hive received your {mode} query. Real reasoning ships in Phase 1c+. " +
                           $"Prompt: \"{Truncate(body.Prompt.Instruction, 200)}\"",
            StructuredBlocks = new List<StructuredBlock>(),
            Usage = new UsageBlock
            {
                InputTokens  = body.Prompt.Instruction.Length / 4,
                OutputTokens = 32,
                CycleId      = $"stub-{DateTime.UtcNow:yyyyMMddHHmmss}"
            },
            Source = "dev_fallback"
        };

        return new OkObjectResult(response);
    }

    private static string? ExtractBearer(HttpRequest req)
    {
        if (!req.Headers.TryGetValue("Authorization", out StringValues hdr)) return null;
        var raw = hdr.ToString();
        const string prefix = "Bearer ";
        if (raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return raw.Substring(prefix.Length).Trim();
        return null;
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "…");
}
