// ============================================================
// FILE        : BEV Hive Frame Publish.cs
// STATUS      : Phase 1g — inbound frame ingress for NEXUS-produced frames
// PURPOSE     : Lets a NEXUS surface push a frame to the rail. The Hive
//               generates some frames itself (fleet.roster, fleet.aggregate,
//               header.assimilated); others originate in NEXUS in-process
//               state that the Hive cannot read (option B, no IPC) — e.g.
//               replication.config (role/copy/risk) and, next, Seven
//               proposal frames. Those producers POST here; the Hive
//               authenticates the dashboard JWT, scopes to the operator's
//               granted tenants, and relays the frame to the tenant group.
//
// CONTRACT    : POST /v1/frame/publish   (Bearer = NEXUS dashboard JWT)
//   body (snapshot frame): { "panel_id":"...", "payload":{...},
//                            "tenant":"<optional, must be a granted tenant>" }
//   body (row-delta):      { "panel_id":"...", "row_key":"...",
//                            "payload":{...}|null, "tenant":"..." }
//   payload:null on a row-delta = tombstone (delete the row) — same rule
//   the shell already honors for fleet.roster.
//
// SCOPE       : the frame is relayed ONLY to t:{tenant}. If "tenant" is
//   omitted and the operator has exactly one grant, that one is used; if
//   omitted with multiple grants, 400 (must name which). A named tenant
//   outside the operator's grants -> 403 TENANT_OUT_OF_SCOPE (same rule as
//   /v1/invoke). NEXUS cannot publish into a tenant it cannot see.
//
// ALLOW-LIST  : only panel_ids NEXUS is permitted to author are accepted,
//   so this ingress cannot be used to spoof Hive-owned frames (a NEXUS
//   push of fleet.aggregate, say, is rejected). Hive-owned ids stay
//   Hive-only.
// ============================================================

using System.Net;
using System.Text.Json;
using BEV.Hive.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace BEV.Hive.Functions;

public sealed class FramePublishFunction
{
    private readonly IJwtValidator _jwt;
    private readonly ISignalRService _sr;
    private readonly ILogger<FramePublishFunction> _log;

    // panel_ids a NEXUS surface is allowed to author. Hive-owned frames
    // (fleet.roster, fleet.aggregate, header.assimilated) are NOT here —
    // they cannot be pushed in from outside.
    private static readonly HashSet<string> NexusAuthorable = new(StringComparer.Ordinal)
    {
        "replication.config",   // Build 2 — per-account role/copy/risk
        "proposals.pending",    // Build 3 — Local Seven proposals
        "seven.thread"          // Build 3 — Seven conversation
    };

    public FramePublishFunction(IJwtValidator jwt, ISignalRService sr, ILogger<FramePublishFunction> log)
    { _jwt = jwt; _sr = sr; _log = log; }

    [Function("FramePublish")]
    public async Task<HttpResponseData> Publish(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "frame/publish")] HttpRequestData req,
        CancellationToken ct)
    {
        // ---- auth: NEXUS presents its dashboard JWT ----
        string token = "";
        if (req.Headers.TryGetValues("Authorization", out var av))
        {
            var a = global::System.Linq.Enumerable.FirstOrDefault(av) ?? "";
            if (a.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = a.Substring(7).Trim();
        }
        var claims = await _jwt.ValidateAsync(token, ct);
        if (!claims.Valid || claims.Role != "dashboard")
            return await Fail(req, HttpStatusCode.Unauthorized,
                claims.Valid ? "NOT_DASHBOARD" : (claims.Error ?? "AUTH_FAILED"), ct);

        if (!_sr.Configured)
            return await Fail(req, HttpStatusCode.ServiceUnavailable, "REALTIME_NOT_CONFIGURED", ct);

        // ---- parse body ----
        string body;
        using (var r = new StreamReader(req.Body)) body = await r.ReadToEndAsync(ct);
        JsonElement root;
        try { using var doc = JsonDocument.Parse(body); root = doc.RootElement.Clone(); }
        catch { return await Fail(req, HttpStatusCode.BadRequest, "BAD_JSON", ct); }

        var panelId = GetStr(root, "panel_id");
        if (panelId.Length == 0)
            return await Fail(req, HttpStatusCode.BadRequest, "MISSING_PANEL_ID", ct);
        if (!NexusAuthorable.Contains(panelId))
            return await Fail(req, HttpStatusCode.Forbidden, "PANEL_NOT_AUTHORABLE", ct);

        // ---- tenant scoping (same rule as /v1/invoke) ----
        var grants = claims.Tenants ?? (IReadOnlyList<string>)Array.Empty<string>();
        var tenant = GetStr(root, "tenant");
        if (tenant.Length == 0)
        {
            if (grants.Count == 1) tenant = grants[0];
            else return await Fail(req, HttpStatusCode.BadRequest, "TENANT_REQUIRED", ct);
        }
        if (!global::System.Linq.Enumerable.Contains(grants, tenant))
            return await Fail(req, HttpStatusCode.Forbidden, "TENANT_OUT_OF_SCOPE", ct);

        // ---- build the rail envelope (snapshot vs row-delta) ----
        var ts = DateTime.UtcNow.ToString("o");
        var hasPayload = root.TryGetProperty("payload", out var payloadEl);
        object payload = hasPayload ? JsonElementToObject(payloadEl) : null!;
        object envelope;
        if (root.TryGetProperty("row_key", out var rkEl) && rkEl.ValueKind == JsonValueKind.String)
        {
            // row-delta; payload:null is a valid tombstone (delete the row)
            envelope = new { panel_id = panelId, row_key = rkEl.GetString(), payload, ts_utc = ts };
        }
        else
        {
            envelope = new { panel_id = panelId, payload, ts_utc = ts };
        }

        await _sr.SendToGroupAsync(RealtimeFunctions.HubName,
            RealtimeFunctions.TenantGroup(tenant), envelope, ct);

        var ok = req.CreateResponse(HttpStatusCode.OK);
        await ok.WriteAsJsonAsync(new { ok = true, panel_id = panelId, tenant, ts_utc = ts }, ct);
        return ok;
    }

    private static async Task<HttpResponseData> Fail(HttpRequestData req, HttpStatusCode code, string error, CancellationToken ct)
    {
        var resp = req.CreateResponse(code);
        await resp.WriteAsJsonAsync(new { ok = false, error }, ct);
        return resp;
    }

    private static string GetStr(JsonElement o, string key)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.String
           ? (e.GetString() ?? "") : "";

    // pass the payload through to the rail without reshaping it
    private static object JsonElementToObject(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var d = new Dictionary<string, object?>();
                foreach (var p in e.EnumerateObject()) d[p.Name] = JsonElementToObject(p.Value);
                return d;
            case JsonValueKind.Array:
                var l = new List<object?>();
                foreach (var i in e.EnumerateArray()) l.Add(JsonElementToObject(i));
                return l;
            case JsonValueKind.String: return e.GetString()!;
            case JsonValueKind.Number: return e.TryGetInt64(out var n) ? n : e.GetDecimal();
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            default: return null!;
        }
    }
}
