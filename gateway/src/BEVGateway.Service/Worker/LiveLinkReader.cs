// ============================================================
// FILE        : LiveLinkReader.cs
// STATUS      : Phase 1e — BEV LiveLink ingestion
// PURPOSE     : Replaces the stub HUD push. Tails the per-box
//               Relay\FLEET\HUD.*.json files EAGLE writes (~1Hz, one
//               per instance), deserializes each into BevLiveSnapshot,
//               normalizes the MID to canonical C-, drops stale files,
//               and forwards each live snapshot to Hive (/v1/fleet/live)
//               for FleetView. Read-only; never blocks HUD/commands.
//
// Mirrors the audit shipper's discovery: enumerate C:\Users\* for the
// FLEET dir (the service runs as LocalSystem, can't use %USERPROFILE%).
// ============================================================

using System.Text.Json;
using BEVGateway.Shared;
using BEVGateway.Shared.Wire;
using BEVGateway.Service.Net;
using Microsoft.Extensions.Logging;

namespace BEVGateway.Service.Worker;

public sealed class LiveLinkReader
{
    private readonly IHiveClient _hive;
    private readonly ILogger _log;
    private readonly StatusReporter? _status;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public LiveLinkReader(IHiveClient hive, ILogger log, StatusReporter? status = null)
    { _hive = hive; _log = log; _status = status; }

    public async Task RunAsync(Func<(string bearer, string mid)?> identity, CancellationToken ct)
    {
        _log.LogInformation("LiveLinkReader started. Scanning C:\\Users\\*\\{Rel}\\{Glob}.",
            GatewayConstants.LiveLinkRelDir, GatewayConstants.LiveLinkGlob);

        var timer = new PeriodicTimer(GatewayConstants.LiveLinkInterval);
        try
        {
            do { await ScanAsync(identity, ct); }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
    }

    private async Task ScanAsync(Func<(string bearer, string mid)?> identity, CancellationToken ct)
    {
        var id = identity();
        if (id is null) return;

        // Always refresh the global trades-assimilated count (PHX/DRG
        // number), even on boxes with no LiveLink dirs to forward.
        var (gotCount, count) = await _hive.GetAssimilatedAsync(id.Value.bearer, id.Value.mid, ct);
        if (gotCount) _status?.Update(s => s.TradesAssimilated = count);

        var dirs = GatewayConstants.DiscoverLiveLinkDirs().ToList();
        if (dirs.Count == 0) return;

        int ok = 0, stale = 0, bad = 0;
        foreach (var dir in dirs)
        {
            string[] files;
            try { files = Directory.GetFiles(dir, GatewayConstants.LiveLinkGlob); }
            catch { continue; }

            foreach (var path in files)
            {
                if (ct.IsCancellationRequested) break;
                BevLiveSnapshot? snap;
                try
                {
                    string text;
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var sr = new StreamReader(fs))
                        text = await sr.ReadToEndAsync(ct);
                    snap = JsonSerializer.Deserialize<BevLiveSnapshot>(text, JsonOpts);
                }
                catch { bad++; continue; }
                if (snap is null) { bad++; continue; }

                // stale check — EAGLE leaves the file after it stops
                if (IsStale(snap.Timestamp)) { stale++; continue; }

                // ONE box = ONE identity. Always stamp the Gateway's own
                // bound MID (normalized C-), ignoring whatever EAGLE wrote
                // in the payload — EAGLE's machine_id can differ from the
                // Gateway's provisioned MID, which would split one physical
                // box into two fleet entries. The bound MID is canonical.
                snap.Mid = NormalizeMid(id.Value.mid);
                // vanity VPS tag (live-only, read from the local tag file).
                snap.CubeTag = GatewayConstants.ReadCubeTag();

                var (pushed, err) = await _hive.PushLiveAsync(id.Value.bearer, id.Value.mid, snap, ct);
                if (pushed) ok++;
                else _log.LogWarning("LiveLinkReader: push failed for {Inst} ({Err}).", snap.InstanceId, err);
            }
        }
        if (ok + stale + bad > 0)
        {
            _log.LogInformation("LiveLink cycle: pushed={Ok} stale={Stale} bad={Bad}.", ok, stale, bad);
            var nowIso = DateTime.UtcNow.ToString("o");
            _status?.Update(s => { s.LastLiveUtc = nowIso; s.LastLivePushed = ok; });
        }
    }

    private static bool IsStale(string? ts)
    {
        if (string.IsNullOrWhiteSpace(ts)) return true;
        if (!DateTime.TryParse(ts, null, global::System.Globalization.DateTimeStyles.AdjustToUniversal, out var t))
            return false;                       // unparseable -> let Hive decide
        return DateTime.UtcNow - t > GatewayConstants.LiveLinkStaleAfter;
    }

    // Canonical platform MID: C-XXXXXX (strip any existing C-, re-add one).
    private static string NormalizeMid(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        var s = raw.Trim().ToUpperInvariant();
        while (s.StartsWith("C-")) s = s.Substring(2);
        return s.Length == 0 ? raw : "C-" + s;
    }
}
