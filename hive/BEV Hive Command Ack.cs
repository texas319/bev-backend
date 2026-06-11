// ============================================================
// FILE        : BEV Hive Command Ack.cs
// STATUS      : Phase 1c-1 — Hive delta for Gateway lifecycle
// LAST UPD    : 2026-05-27 14:00 CST
// PURPOSE     : POST /v1/command-ack. Gateway acknowledges
//               command execution outcome. Hive records result,
//               detail, and executed_utc on the CommandDoc.
//               Used by future fleet admin UI to see what
//               actually happened to a queued command.
// OWNS        : Command outcome recording.
// CALLED BY   : Gateway after executing each command.
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

public sealed class CommandAckFunction
{
    private readonly IJwtValidator _jwt;
    private readonly IHiveStorage _storage;
    private readonly ILogger<CommandAckFunction> _log;

    private static readonly HashSet<string> AllowedResults = new()
    {
        "success", "failed", "skipped", "expired"
    };

    public CommandAckFunction(IJwtValidator jwt, IHiveStorage storage, ILogger<CommandAckFunction> log)
    {
        _jwt = jwt;
        _storage = storage;
        _log = log;
    }

    [Function("CommandAck")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "command-ack")] HttpRequest req,
        CancellationToken ct)
    {
        // ---- 1. Auth ----
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

        // ---- 2. Parse body ----
        CommandAckRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<CommandAckRequest>(req.Body, cancellationToken: ct);
        }
        catch (JsonException)
        {
            return new BadRequestObjectResult(new ErrorResponse { Error = "MALFORMED_REQUEST" });
        }

        if (body is null || string.IsNullOrEmpty(body.CommandId) || string.IsNullOrEmpty(body.Result))
            return new BadRequestObjectResult(new ErrorResponse { Error = "MALFORMED_REQUEST" });

        if (!AllowedResults.Contains(body.Result))
            return new BadRequestObjectResult(new ErrorResponse
            {
                Error = "INVALID_RESULT",
                Message = $"Result must be one of: {string.Join(", ", AllowedResults)}"
            });

        var executedUtc = string.IsNullOrEmpty(body.ExecutedUtc)
            ? DateTime.UtcNow.ToString("o")
            : body.ExecutedUtc;

        // ---- 3. Update command row in Cosmos ----
        var doc = await _storage.AckCommandAsync(
            body.CommandId, claims.TenantId,
            body.Result, body.Detail, executedUtc, ct);

        if (doc is null)
        {
            // Command not found — could be TTL'd or never existed.
            // Don't error; respond 200 so the Gateway doesn't loop
            // retrying. Just record the discrepancy in logs.
            _log.LogWarning("Ack for unknown command: tenant={Tenant} mid={Mid} cmd={Cmd}",
                claims.TenantId, claims.MachineId, body.CommandId);
        }
        else
        {
            _log.LogInformation("Command acked: cmd={Cmd} kind={Kind} result={Result}",
                body.CommandId, doc.Kind, body.Result);
        }

        return new OkObjectResult(new CommandAckResponse
        {
            ReceivedUtc = DateTime.UtcNow.ToString("o")
        });
    }
}
