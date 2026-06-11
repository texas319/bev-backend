// ============================================================
// FILE        : BEV Hive Commands.cs
// STATUS      : Phase 1c-1 — Hive delta for Gateway lifecycle
// LAST UPD    : 2026-05-27 14:00 CST
// PURPOSE     : GET /v1/commands. Gateway long-polls for pending
//               commands. Server holds the request open for up
//               to ~30s (Function timeout is 45s, leave headroom)
//               waiting for a command to land for this MID. If
//               nothing lands by timeout, returns an empty array
//               and a fresh since_utc cursor.
//
//               Sprint 1: only PING and REFRESH_CREDENTIALS will
//               be enqueued. Other kinds defer to Sprint 3.
//
//               Issuing commands is a separate admin surface
//               (POST /v1/admin/command) not in this build —
//               for now, drop rows directly into Cosmos `cycles`
//               container to test.
// OWNS        : Command delivery to Gateway.
// CALLED BY   : Gateway Windows service in a loop.
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

namespace BEV.Hive.Functions;

public sealed class CommandsFunction
{
    private readonly IJwtValidator _jwt;
    private readonly IHiveStorage _storage;
    private readonly ILogger<CommandsFunction> _log;

    // Total long-poll budget. Stay below the Functions execution
    // timeout (45s in host.json) with margin. 30s is plenty —
    // Gateway re-polls immediately on empty return.
    private static readonly TimeSpan LongPollBudget = TimeSpan.FromSeconds(30);

    // How often we re-check Cosmos for new commands during the
    // poll. Lower = faster delivery, higher = less Cosmos load.
    // 2s is a reasonable starting balance.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    public CommandsFunction(IJwtValidator jwt, IHiveStorage storage, ILogger<CommandsFunction> log)
    {
        _jwt = jwt;
        _storage = storage;
        _log = log;
    }

    [Function("Commands")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "commands")] HttpRequest req,
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

        // ---- 2. since_utc cursor ----
        // Defaults to 5 minutes ago if not provided. Caps at 24h
        // ago to keep queries cheap.
        var sinceParam = req.Query["since_utc"].ToString();
        DateTime since;
        if (!string.IsNullOrEmpty(sinceParam) && DateTime.TryParse(sinceParam, out var parsed))
        {
            since = parsed.ToUniversalTime();
            var floor = DateTime.UtcNow.AddHours(-24);
            if (since < floor) since = floor;
        }
        else
        {
            since = DateTime.UtcNow.AddMinutes(-5);
        }

        // ---- 3. Long-poll loop ----
        var deadline = DateTime.UtcNow + LongPollBudget;
        List<CommandDoc> pending = new();

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            pending = await _storage.FindPendingCommandsAsync(
                claims.TenantId, claims.MachineId, since, ct);
            if (pending.Count > 0) break;

            try { await Task.Delay(PollInterval, ct); }
            catch (TaskCanceledException) { break; }
        }

        // ---- 4. Mark delivered for any that we're returning ----
        foreach (var cmd in pending)
        {
            await _storage.MarkDeliveredAsync(cmd.Id, claims.TenantId, ct);
        }

        // ---- 5. Build response ----
        var response = new CommandsResponse
        {
            Commands = pending.Select(c => new GatewayCommand
            {
                CommandId  = c.Id,
                IssuedUtc  = c.IssuedUtc,
                Kind       = c.Kind,
                Args       = c.Args ?? new Dictionary<string, object>(),
                ExpiresUtc = c.ExpiresUtc
            }).ToList(),
            NextSinceUtc = DateTime.UtcNow.ToString("o")
        };

        _log.LogInformation("Commands poll: tenant={Tenant} mid={Mid} returned={Count}",
            claims.TenantId, claims.MachineId, pending.Count);

        return new OkObjectResult(response);
    }
}
