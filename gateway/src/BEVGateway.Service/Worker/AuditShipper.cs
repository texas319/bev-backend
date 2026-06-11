// ============================================================
// FILE        : AuditShipper.cs
// STATUS      : Phase 2 — Gateway audit shipper (production feed)
// PURPOSE     : Tails the EAGLE audit CSV directory and POSTs each
//               complete/rotated file to Hive /v1/audit/ingest using
//               the Gateway's bound JWT + MID. This is the production
//               replacement for the manual ingest used to validate
//               the pipeline. Runs as a parallel loop off GatewayWorker
//               (like the command poll), never blocking HUD cadence.
//
// RISK VISIBILITY ITEMS (addressed by design):
//   * Backpressure: if Hive is unavailable the scan logs and returns;
//     nothing is marked shipped, so it retries next cycle. At most
//     AuditMaxPerCycle files per scan to avoid flooding on first run.
//   * Failed-parse: a 500 from Hive (single bad file) is logged and
//     the file is NOT marked shipped — it retries on a later scan/
//     build. It never blocks the rest of the batch. (Hive side also
//     hardened to not leave a dedup-blocking ledger row on FAILED.)
//   * Concurrent multi-box: each box ships under its own MID/JWT to
//     a stateless endpoint; Hive dedups by content hash. No client
//     serialization needed; boxes are independent.
//   * Bandwidth/cost: only files unmodified for AuditQuietPeriod are
//     shipped, once each (local ship-state + Hive content dedup), so
//     steady-state traffic is one POST per rotated CSV, not re-sends.
//
// ROTATION SAFETY: a file must be unmodified for AuditQuietPeriod
// before shipping, so we never POST a CSV EAGLE is still writing.
// ============================================================

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BEVGateway.Shared;
using BEVGateway.Shared.Wire;
using BEVGateway.Service.Net;
using Microsoft.Extensions.Logging;

namespace BEVGateway.Service.Worker;

public sealed class AuditShipper
{
    private readonly IHiveClient _hive;
    private readonly ILogger _log;
    private readonly StatusReporter? _status;
    private long _shipTotalOk;

    // path -> last shipped signature (size:mtimeTicks). If a file changes
    // after shipping (EAGLE appended), the signature differs and we ship
    // again; Hive dedups identical content by SHA so re-ships are cheap.
    private readonly ConcurrentDictionary<string, string> _shipped = new();
    private DateTime _lastStateFlush = DateTime.MinValue;

    public AuditShipper(IHiveClient hive, ILogger log, StatusReporter? status = null)
    {
        _hive = hive;
        _log = log;
        _status = status;
        LoadState();
    }

    /// <summary>
    /// Parallel loop. Scans the audit dir every AuditScanInterval and ships
    /// new/changed, quiesced files. Pulls the live bearer/mid each cycle via
    /// the supplied accessors so a JWT refresh on the main loop is picked up.
    /// </summary>
    public async Task RunAsync(Func<(string bearer, string mid)?> identityAccessor,
        CancellationToken ct)
    {
        var timer = new PeriodicTimer(GatewayConstants.AuditScanInterval);
        _log.LogInformation("AuditShipper started. Scanning C:\\Users\\*\\{Rel} (recurse={Rec}).",
            GatewayConstants.AuditLogRelPath, GatewayConstants.AuditRecurse);

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var id = identityAccessor();
                if (id is null) continue;                 // not provisioned yet
                await ScanAndShipAsync(id.Value.bearer, id.Value.mid, ct);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        finally
        {
            FlushState();
            _log.LogInformation("AuditShipper stopped.");
        }
    }

    private async Task ScanAndShipAsync(string bearer, string mid, CancellationToken ct)
    {
        // Discover the operator audit dir(s) under C:\Users\* (the service
        // runs as LocalSystem, so we can't use %USERPROFILE%).
        var dirs = GatewayConstants.DiscoverAuditLogDirs().ToList();
        if (dirs.Count == 0) return;                       // no EAGLE audit dir yet

        var allFiles = new List<string>();
        var opt = GatewayConstants.AuditRecurse
            ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (var dir in dirs)
        {
            try { allFiles.AddRange(Directory.EnumerateFiles(dir, "*.CSV", opt)); }
            catch (Exception ex) { _log.LogWarning(ex, "AuditShipper: enumerate failed for {Dir}.", dir); }
        }
        if (allFiles.Count == 0) return;

        var now = DateTime.UtcNow;
        int shippedThisCycle = 0, ok = 0, dup = 0, failed = 0;

        foreach (var path in allFiles)
        {
            if (ct.IsCancellationRequested) break;
            if (shippedThisCycle >= GatewayConstants.AuditMaxPerCycle) break; // backpressure

            FileInfo fi;
            try { fi = new FileInfo(path); } catch { continue; }
            if (!fi.Exists) continue;

            // Rotation safety: skip files still being written.
            if ((now - fi.LastWriteTimeUtc) < GatewayConstants.AuditQuietPeriod) continue;

            var sig = $"{fi.Length}:{fi.LastWriteTimeUtc.Ticks}";
            if (_shipped.TryGetValue(path, out var prev) && prev == sig) continue; // already shipped

            string content;
            try { content = await ReadAllTextAsync(path, ct); }
            catch (Exception ex) { _log.LogWarning(ex, "AuditShipper: read failed {File}", fi.Name); continue; }

            var req = new AuditIngestRequest { FileName = fi.Name, Content = content };
            var (okResp, body, error) = await _hive.ShipAuditAsync(bearer, mid, req, ct);

            if (!okResp)
            {
                if (error == "AUTH_EXPIRED")
                {
                    // Token rolled mid-cycle; stop this scan, main loop refreshes.
                    _log.LogInformation("AuditShipper: auth expired mid-scan; will resume next cycle.");
                    break;
                }
                // Transport down (Hive unreachable) or a 500 on this file:
                // do NOT mark shipped — retried next scan. Don't block batch.
                failed++;
                if (error != null && error.StartsWith("HTTP_5"))
                    _log.LogWarning("AuditShipper: Hive rejected {File} ({Err}); will retry.", fi.Name, error);
                // If transport failure, Hive is likely down — stop the cycle
                // entirely (backpressure) rather than hammering every file.
                if (error == "TRANSPORT_FAILURE") break;
                continue;
            }

            // Success — mark shipped (OK or DUPLICATE both count as done).
            _shipped[path] = sig;
            shippedThisCycle++;
            if (body?.Status == "DUPLICATE") dup++;
            else { ok++; }
        }

        if (shippedThisCycle > 0 || failed > 0)
        {
            _log.LogInformation("AuditShipper cycle: shipped={S} ok={Ok} dup={Dup} failed={F}",
                shippedThisCycle, ok, dup, failed);
            MaybeFlushState();
            _shipTotalOk += ok;
            var nowIso = DateTime.UtcNow.ToString("o");
            _status?.Update(s =>
            {
                s.LastShipUtc    = nowIso;
                s.LastShipOk     = ok;
                s.LastShipDup    = dup;
                s.LastShipFailed = failed;
                s.ShipTotalOk    = _shipTotalOk;
            });
        }
    }

    // ---- local ship-state persistence (survive service restart) ----

    private void LoadState()
    {
        try
        {
            var p = GatewayConstants.AuditShipStatePath;
            if (!File.Exists(p)) return;
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(p));
            if (map != null) foreach (var kv in map) _shipped[kv.Key] = kv.Value;
            _log.LogInformation("AuditShipper: loaded {N} prior ship records.", _shipped.Count);
        }
        catch (Exception ex) { _log.LogWarning(ex, "AuditShipper: state load failed (starting fresh)."); }
    }

    private void MaybeFlushState()
    {
        if ((DateTime.UtcNow - _lastStateFlush) < TimeSpan.FromMinutes(1)) return;
        FlushState();
    }

    private void FlushState()
    {
        try
        {
            Directory.CreateDirectory(GatewayConstants.IdentityDir);
            var json = JsonSerializer.Serialize(new Dictionary<string, string>(_shipped));
            File.WriteAllText(GatewayConstants.AuditShipStatePath, json);
            _lastStateFlush = DateTime.UtcNow;
        }
        catch (Exception ex) { _log.LogWarning(ex, "AuditShipper: state flush failed."); }
    }

    private static async Task<string> ReadAllTextAsync(string path, CancellationToken ct)
    {
        // Open shared so we don't fight EAGLE's writer; read as UTF-8 (BOM ok).
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await sr.ReadToEndAsync(ct);
    }
}
