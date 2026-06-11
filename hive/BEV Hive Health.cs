// ============================================================
// FILE        : BEV Hive Health.cs
// STATUS      : Phase 1b — Hive /v1/seven/query stub
// LAST UPD    : 2026-05-24 13:00 CST
// PURPOSE     : GET /v1/health. Anonymous liveness check that
//               returns a small payload with build label + UTC.
//               Lets monitoring + Gateway confirm Hive is up
//               without needing a valid token.
// OWNS        : Liveness surface.
// CALLED BY   : External monitors, Gateway heartbeat (planned).
// CHANGE LOG  :
//   2026-05-24 13:00 CST  v0-26.0524-B  Initial scaffold (Phase 1b).
// ============================================================

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace BEV.Hive.Functions;

public sealed class HealthFunction
{
    [Function("Health")]
    public IActionResult Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequest req)
    {
        return new OkObjectResult(new
        {
            ok        = true,
            service   = "bev-hive",
            build     = Environment.GetEnvironmentVariable("HIVE_BUILD_LABEL") ?? "HV.unknown",
            now_utc   = DateTime.UtcNow.ToString("o")
        });
    }
}
