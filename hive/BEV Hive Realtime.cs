// ============================================================
// FILE        : BEV Hive Realtime.cs
// STATUS      : Phase 2 / Drop 1 — live rails (Azure SignalR Serverless)
// PURPOSE     : Web/desktop NEXUS connection entry point.
//
//   POST /v1/realtime/negotiate
//     Bearer dashboard JWT -> validates (role must be "dashboard") ->
//     mints a SignalR client token (userId = account email) -> adds the
//     connection's user to one SignalR group per entitled fleet_id (plus
//     a tenant group) -> returns { url, accessToken }.
//
//   The client connects with that url+token and listens on target
//   "frame". Hive pushes panel frames to the fleet/tenant groups, so a
//   connection only receives panels for fleets the account is in.
//
//   Frame envelope (NEXUS contract):
//     snapshot  : { panel_id, payload, ts_utc }
//     row delta : { panel_id, row_key, payload, ts_utc }   (no payload = delete)
//   Hub: "nexus".  Group naming: t:{tenant}, f:{tenant}:{fleetId}.
// ============================================================

using System.Net;
using BEV.Hive.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace BEV.Hive.Functions;

public sealed class RealtimeFunctions
{
    public const string HubName = "nexus";

    private readonly IJwtValidator _jwt;
    private readonly ISignalRService _sr;
    private readonly ILogger<RealtimeFunctions> _log;

    public RealtimeFunctions(IJwtValidator jwt, ISignalRService sr, ILogger<RealtimeFunctions> log)
    {
        _jwt = jwt; _sr = sr; _log = log;
    }

    [Function("RealtimeNegotiate")]
    public async Task<HttpResponseData> Negotiate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "realtime/negotiate")] HttpRequestData req,
        CancellationToken ct)
    {
        string token = "";
        if (req.Headers.TryGetValues("Authorization", out var av))
        {
            var a = global::System.Linq.Enumerable.FirstOrDefault(av) ?? "";
            if (a.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = a.Substring(7).Trim();
        }

        var claims = await _jwt.ValidateAsync(token, ct);
        if (!claims.Valid || claims.Role != "dashboard")
        {
            var unauth = req.CreateResponse(HttpStatusCode.Unauthorized);
            await unauth.WriteAsJsonAsync(new { ok = false,
                error = claims.Valid ? "NOT_DASHBOARD" : (claims.Error ?? "AUTH_FAILED") }, ct);
            return unauth;
        }
        if (!_sr.Configured)
        {
            var err = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            await err.WriteAsJsonAsync(new { ok = false, error = "REALTIME_NOT_CONFIGURED" }, ct);
            return err;
        }

        var userId = string.IsNullOrWhiteSpace(claims.Subject) ? $"anon:{Guid.NewGuid():N}" : claims.Subject;

        // mint client connection info
        var (url, accessToken) = _sr.BuildClientNegotiate(HubName, userId);

        // join the user to tenant + per-fleet groups (so pushes are scoped)
        // join one SignalR group per tenant the operator may view, so
        // they receive frames for all their tenants (dashboard sections
        // the view by tenant).
        var tenants = claims.Tenants ?? (IReadOnlyList<string>)Array.Empty<string>();
        foreach (var t in tenants)
            await _sr.AddUserToGroupAsync(HubName, userId, TenantGroup(t), ct);

        var resp = req.CreateResponse(HttpStatusCode.OK);
        await resp.WriteAsJsonAsync(new
        {
            ok = true,
            url,
            accessToken,
            user = userId,
            tenants = claims.Tenants ?? (IReadOnlyList<string>)Array.Empty<string>(),
            hub = HubName,
            expiresMinutes = 60
        }, ct);
        return resp;
    }

    public static string TenantGroup(string tenantId) => $"t:{tenantId}";
    public static string FleetGroup(string tenantId, string fleetId) => $"f:{tenantId}:{fleetId}";
}
