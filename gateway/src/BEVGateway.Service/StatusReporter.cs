// ============================================================
// FILE        : StatusReporter.cs
// STATUS      : Phase 1c-2 — Gateway + Tray binary
// LAST UPD    : 2026-05-27 15:00 CST
// PURPOSE     : Single in-memory snapshot of Gateway health.
//               Worker writes; IPC server reads on demand.
//               Thread-safe via lock — read frequency is low
//               (tray polls every 5-10s).
// OWNS        : Live status snapshot.
// CALLED BY   : GatewayWorker (writes), TrayIpcServer (reads).
// ============================================================

using BEVGateway.Shared;
using BEVGateway.Shared.Ipc;

namespace BEVGateway.Service;

public sealed class StatusReporter
{
    private readonly object _lock = new();
    private StatusSnapshot _snapshot = new()
    {
        Ok          = true,
        Build       = GatewayConstants.GatewayBuild,
        Health      = ConnectionHealth.Unknown,
        StatusText  = "Starting up..."
    };

    public StatusSnapshot Get()
    {
        lock (_lock) return Clone(_snapshot);
    }

    public void Update(Action<StatusSnapshot> mutate)
    {
        lock (_lock)
        {
            mutate(_snapshot);
            _snapshot.Health = ComputeHealth(_snapshot);
            _snapshot.StatusText = ComputeText(_snapshot);
        }
    }

    private static ConnectionHealth ComputeHealth(StatusSnapshot s)
    {
        // Health model (GW.0602.26-J): SERVER is the anchor. Hive is
        // enrichment — its connection riding up and down must never blink the
        // trader's status indicator. The rule:
        //   • Not provisioned, server down, or token expired -> Red.
        //   • Server up + token healthy + Hive heard from recently -> Green.
        //   • Hive genuinely silent past the grace window -> Yellow (degraded,
        //     not down) — but only because enrichment is stale, never Red on
        //     Hive alone.
        //   • Token inside the 60-min refresh lead -> Yellow.
        // The key change vs prior builds: we no longer go Yellow the instant
        // the HiveUp flag toggles. We look at LastHudUtc (last successful Hive
        // contact). As long as that's within HiveGraceWindow, Hive counts as
        // up regardless of one or two missed pushes. This is what stops the
        // amber flash every few minutes when Hive has a momentary gap.
        if (!s.Provisioned)          return ConnectionHealth.Red;
        if (!s.ServerUp)             return ConnectionHealth.Red;
        if (s.TokenMinutesLeft <= 0) return ConnectionHealth.Red;

        if (s.TokenMinutesLeft <= 60) return ConnectionHealth.Yellow;

        // Hive: judged by recency of last good contact, not the instantaneous
        // flag. A blip leaves LastHudUtc fresh, so we stay Green.
        if (!HiveHeardRecently(s))   return ConnectionHealth.Yellow;

        return ConnectionHealth.Green;
    }

    // Hive counts as "up" if we've had a successful HUD push within this
    // window. At a 15s push cadence with 2-failure hysteresis, a real outage
    // takes well over a minute to register here — and a single dropped push
    // never does. Generous on purpose: enrichment staleness is not urgent.
    private static readonly TimeSpan HiveGraceWindow = TimeSpan.FromMinutes(3);

    private static bool HiveHeardRecently(StatusSnapshot s)
    {
        if (string.IsNullOrEmpty(s.LastHudUtc)) return s.HiveUp; // pre-first-push fallback
        if (DateTime.TryParse(
                s.LastHudUtc, null,
                global::System.Globalization.DateTimeStyles.RoundtripKind,
                out var last))
        {
            return (DateTime.UtcNow - last.ToUniversalTime()) <= HiveGraceWindow;
        }
        return s.HiveUp; // unparseable -> fall back to the flag
    }

    private static string ComputeText(StatusSnapshot s)
    {
        if (!s.Provisioned) return "Not provisioned. Run setup.";
        return s.Health switch
        {
            ConnectionHealth.Green   => "All systems normal.",
            ConnectionHealth.Yellow  => s.TokenMinutesLeft <= 60
                ? $"JWT expires in {s.TokenMinutesLeft}m; auto-refresh pending."
                : "Server up; Hive enrichment stale.",
            ConnectionHealth.Red     => "Connection down. See log for detail.",
            _                        => "Initializing..."
        };
    }

    private static StatusSnapshot Clone(StatusSnapshot s) => new()
    {
        Ok               = s.Ok,
        Build            = s.Build,
        Provisioned      = s.Provisioned,
        TenantId         = s.TenantId,
        MachineId        = s.MachineId,
        Tier             = s.Tier,
        NodeClass        = s.NodeClass,
        FleetRole        = s.FleetRole,
        TokenExpUtc      = s.TokenExpUtc,
        TokenMinutesLeft = s.TokenMinutesLeft,
        ServerUp         = s.ServerUp,
        HiveUp           = s.HiveUp,
        LastHudUtc       = s.LastHudUtc,
        LastHudStatus    = s.LastHudStatus,
        LastCommandUtc   = s.LastCommandUtc,
        Health           = s.Health,
        StatusText       = s.StatusText,
        Error            = s.Error
    };
}
