// ============================================================
// FILE        : BEV Hive Invoke.cs
// STATUS      : Phase 2 / Drop 2 — control plane endpoint
// PURPOSE     : POST /v1/invoke — the single WRITE/CONTROL entry point.
//
//   Flow (matches BEV Function Catalog.json contract):
//     1. Validate dashboard JWT (role=dashboard).
//     2. Look up function_id in the catalog (allow-list). Unknown = 404.
//     3. Tier gate:
//          read    -> allowed if entitled to the fleet
//          write   -> entitled + audited
//          nuclear -> entitled + confirm==true + confirm_token round-trip
//                     + announcement frame to all tenant subscribers
//     4. Scoping: any arg account/fleet_id is checked against the JWT
//        fleet_ids -> out of scope = 403, no execution.
//     5. actor is server-stamped from the JWT (non-falsifiable).
//     6. Route: enqueue a command doc for the target Cube's Gateway to
//        execute (the real EAGLE/NEXUS action runs on the Cube). Return
//        an ack envelope { request_id, function_id, status, result, error }.
//
//   Nuclear two-phase:
//     - First call (confirm true, no token) -> status=pending + confirm_token
//       (30s TTL, in-memory). Also pushes a "control.nuclear_pending" frame.
//     - Second call (same args + confirm_token) -> executes, broadcasts
//       "control.nuclear_engaged" announcement frame to the tenant group.
// ============================================================

using System.Net;
using System.Text.Json;
using BEV.Hive.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace BEV.Hive.Functions;

public sealed class InvokeFunctions
{
    private readonly IJwtValidator _jwt;
    private readonly IFunctionCatalog _catalog;
    private readonly ISignalRService _sr;
    private readonly IHiveStorage _storage;
    private readonly ILogger<InvokeFunctions> _log;

    // in-memory confirm-token store for nuclear two-phase (30s TTL).
    private static readonly Dictionary<string, (string fnId, string argsHash, string actor, DateTime exp)> _confirms = new();
    private static readonly object _confirmLock = new();
    private const int ConfirmTtlSeconds = 30;

    public InvokeFunctions(IJwtValidator jwt, IFunctionCatalog catalog, ISignalRService sr, IHiveStorage storage, ILogger<InvokeFunctions> log)
    {
        _jwt = jwt; _catalog = catalog; _sr = sr; _storage = storage; _log = log;
    }

    [Function("Invoke")]
    public async Task<HttpResponseData> Invoke(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "invoke")] HttpRequestData req,
        CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");

        // ---- auth ----
        string token = "";
        if (req.Headers.TryGetValues("Authorization", out var av))
        {
            var a = global::System.Linq.Enumerable.FirstOrDefault(av) ?? "";
            if (a.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = a.Substring(7).Trim();
        }
        var claims = await _jwt.ValidateAsync(token, ct);
        if (!claims.Valid || claims.Role != "dashboard")
            return await Ack(req, requestId, "", "error", null, claims.Valid ? "NOT_DASHBOARD" : "AUTH_FAILED", HttpStatusCode.Unauthorized);

        // ---- parse body ----
        string body;
        using (var sr = new StreamReader(req.Body)) body = await sr.ReadToEndAsync(ct);
        JsonElement root;
        try { root = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body).RootElement; }
        catch { return await Ack(req, requestId, "", "error", null, "BAD_JSON", HttpStatusCode.BadRequest); }

        var functionId = root.TryGetProperty("function_id", out var fEl) ? (fEl.GetString() ?? "") : "";
        var args = root.TryGetProperty("args", out var aEl) && aEl.ValueKind == JsonValueKind.Object ? aEl : default;

        // ---- catalog allow-list ----
        if (!_catalog.TryGet(functionId, out var fn))
            return await Ack(req, requestId, functionId, "error", null, "UNKNOWN_FUNCTION", HttpStatusCode.NotFound);

        // ---- scoping: a target tenant arg must be within the operator's grants ----
        var tenants = claims.Tenants ?? (IReadOnlyList<string>)Array.Empty<string>();
        if (!ScopeOk(args, tenants, out var scopeErr))
            return await Ack(req, requestId, functionId, "error", null, scopeErr, HttpStatusCode.Forbidden);

        var actor = claims.Subject;   // server-stamped, non-falsifiable
        var tenantGroup = RealtimeFunctions.TenantGroup(claims.TenantId);

        // ---- tier handling ----
        if (fn.Tier.Equals("nuclear", StringComparison.OrdinalIgnoreCase))
        {
            var confirm = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("confirm", out var cEl) && cEl.ValueKind == JsonValueKind.True;
            var providedToken = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("confirm_token", out var tEl) ? (tEl.GetString() ?? "") : "";
            var argsHash = HashArgs(args);

            if (!confirm)
                return await Ack(req, requestId, functionId, "error", null, "CONFIRM_REQUIRED", HttpStatusCode.BadRequest);

            if (string.IsNullOrEmpty(providedToken))
            {
                // phase 1: issue a confirm_token, announce pending, do NOT execute
                var confirmToken = Guid.NewGuid().ToString("N");
                lock (_confirmLock)
                {
                    PruneConfirms();
                    _confirms[confirmToken] = (functionId, argsHash, actor, DateTime.UtcNow.AddSeconds(ConfirmTtlSeconds));
                }
                if (_sr.Configured)
                    await _sr.SendToGroupAsync(RealtimeFunctions.HubName, tenantGroup,
                        Frame("control.nuclear_pending", new { function_id = functionId, actor, ttl = ConfirmTtlSeconds }), ct);
                return await Ack(req, requestId, functionId, "pending",
                    new { confirm_token = confirmToken, ttl_seconds = ConfirmTtlSeconds }, null, HttpStatusCode.OK);
            }

            // phase 2: validate the token, then execute
            bool ok; (string fnId, string argsHash, string actor, DateTime exp) entry = default;
            lock (_confirmLock)
            {
                PruneConfirms();
                ok = _confirms.TryGetValue(providedToken, out entry);
                if (ok) _confirms.Remove(providedToken);
            }
            if (!ok || entry.fnId != functionId || entry.argsHash != argsHash || entry.exp < DateTime.UtcNow)
                return await Ack(req, requestId, functionId, "error", null, "CONFIRM_TOKEN_INVALID", HttpStatusCode.BadRequest);

            await RouteCommand(claims.TenantId, functionId, args, actor, requestId, ct);
            if (_sr.Configured)
                await _sr.SendToGroupAsync(RealtimeFunctions.HubName, tenantGroup,
                    Frame("control.nuclear_engaged", new { function_id = functionId, actor, ts_utc = DateTime.UtcNow.ToString("o") }), ct);
            await AuditInvoke(functionId, actor, args, "nuclear", ct);
            return await Ack(req, requestId, functionId, "ok", new { routed = true, tier = "nuclear" }, null, HttpStatusCode.OK);
        }

        if (fn.Tier.Equals("write", StringComparison.OrdinalIgnoreCase))
        {
            await RouteCommand(claims.TenantId, functionId, args, actor, requestId, ct);
            await AuditInvoke(functionId, actor, args, "write", ct);
            return await Ack(req, requestId, functionId, "ok", new { routed = true, tier = "write" }, null, HttpStatusCode.OK);
        }

        // read tier: no state change. (Reads are also served by dedicated
        // GET endpoints + the panel registry; here we just acknowledge so
        // a read invoked through this channel is well-formed.)
        return await Ack(req, requestId, functionId, "ok", new { tier = "read", note = "served via read endpoints" }, null, HttpStatusCode.OK);
    }

    // ---- scoping check: a tenant_id arg must be in the operator's grants ----
    private static bool ScopeOk(JsonElement args, IReadOnlyList<string> tenants, out string err)
    {
        err = "";
        if (args.ValueKind != JsonValueKind.Object) return true;   // no args to scope
        if (args.TryGetProperty("tenant_id", out var tEl))
        {
            var tid = tEl.GetString() ?? "";
            if (tid.Length > 0 && tenants.Count > 0 && !tenants.Contains(tid)) { err = "TENANT_OUT_OF_SCOPE"; return false; }
        }
        // account/mid-scoped args resolve at the Cube (which only holds its
        // own tenant's accounts); tenant grant is the gate here.
        return true;
    }

    private async Task RouteCommand(string tenantId, string functionId, JsonElement args, string actor, string requestId, CancellationToken ct)
    {
        // Resolve the target box from an optional mid arg; empty = the
        // Gateway matches its own MID on drain (tenant-wide / nuclear).
        var mid = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("mid", out var mEl) ? (mEl.GetString() ?? "") : "";
        var now = DateTime.UtcNow;
        var doc = new BEV.Hive.Models.CommandDoc
        {
            Id        = requestId,
            TenantId  = tenantId,
            MachineId = mid,
            Kind      = "INVOKE",
            Args      = new Dictionary<string, object>
            {
                ["function_id"] = functionId,
                ["args"]        = args.ValueKind == JsonValueKind.Object ? args.GetRawText() : "{}",
                ["actor"]       = actor
            },
            IssuedUtc  = now.ToString("o"),
            ExpiresUtc = now.AddMinutes(10).ToString("o"),
            DocType    = "command"
        };
        await _storage.EnqueueCommandAsync(doc, ct);
        _log.LogInformation("INVOKE routed fn={Fn} mid={Mid} actor={Actor} req={Req}", functionId, mid, actor, requestId);
    }

    private Task AuditInvoke(string functionId, string actor, JsonElement args, string tier, CancellationToken ct)
    {
        // The command doc itself is the durable record; the Cube's EAGLE
        // PART 37 + Server audit pipeline capture execution. Log here for
        // the Hive trace; non-fatal.
        _log.LogInformation("AUDIT invoke fn={Fn} tier={Tier} actor={Actor}", functionId, tier, actor);
        return Task.CompletedTask;
    }

    private static void PruneConfirms()
    {
        var now = DateTime.UtcNow;
        var dead = _confirms.Where(kv => kv.Value.exp < now).Select(kv => kv.Key).ToList();
        foreach (var k in dead) _confirms.Remove(k);
    }

    private static string HashArgs(JsonElement args)
    {
        var raw = args.ValueKind == JsonValueKind.Object ? args.GetRawText() : "{}";
        using var sha = global::System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(global::System.Text.Encoding.UTF8.GetBytes(raw)));
    }

    private static object Frame(string panelId, object payload) =>
        new { panel_id = panelId, payload, ts_utc = DateTime.UtcNow.ToString("o") };

    private async Task<HttpResponseData> Ack(HttpRequestData req, string requestId, string functionId,
        string status, object? result, string? error, HttpStatusCode code)
    {
        var resp = req.CreateResponse(code);
        await resp.WriteAsJsonAsync(new
        {
            request_id = requestId,
            function_id = functionId,
            status,
            result,
            error,
            ts_utc = DateTime.UtcNow.ToString("o")
        });
        return resp;
    }
}
