// ============================================================
// FILE        : UpdateService.cs
// STATUS      : Phase 2 — Gateway auto-update channel
// PURPOSE     : Checks the Server for a newer Gateway build and
//               self-installs it. Two triggers:
//                 (1) periodic loop (startup + hourly)
//                 (2) pushed UPDATE_GATEWAY command (immediate)
//               Flow: fetch manifest -> compare version to our own
//               GatewayBuild -> if newer, download MSI -> verify
//               SHA256 -> launch a DETACHED installer helper that
//               waits, runs msiexec, and lets the MSI stop/replace/
//               restart this very service.
//
// WHY A DETACHED HELPER: a running Windows service cannot cleanly
// MSI-upgrade itself in-process — the MSI's ServiceControl stops the
// service that launched msiexec, killing the installer mid-run. So we
// write a tiny .cmd to the staging dir and start it with UseShellExecute
// so it outlives this process; it sleeps a few seconds, then msiexec
// performs the MajorUpgrade (stop -> replace -> start) on its own.
//
// SAFETY:
//   * SHA256 verified before install — a truncated/tampered download
//     is never executed.
//   * Loop-protection: we record the attempted version; if we boot up
//     and find we ALREADY tried this exact version (i.e. the install
//     didn't take), we do NOT retry automatically — we log and wait
//     for a newer version or a manual push, so a bad build can't put
//     the box in an install loop.
//   * Version compare is conservative: only strictly-NEWER versions
//     install; equal or older are ignored (no downgrade).
// ============================================================

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BEVGateway.Shared;
using BEVGateway.Shared.Wire;
using BEVGateway.Service.Net;
using Microsoft.Extensions.Logging;

namespace BEVGateway.Service.Worker;

public sealed class UpdateService
{
    private readonly IServerClient _server;
    private readonly StatusReporter _status;
    private readonly ILogger _log;

    public UpdateService(IServerClient server, StatusReporter status, ILogger log)
    {
        _server = server; _status = status; _log = log;
    }

    /// <summary>Periodic loop: check on startup, then every UpdateCheckInterval.</summary>
    public async Task RunAsync(Func<(string bearer, string mid)?> identity, CancellationToken ct)
    {
        _log.LogInformation("UpdateService started (current build {Build}). Check interval {Int}.",
            GatewayConstants.GatewayBuild, GatewayConstants.UpdateCheckInterval);

        // brief settle before first check so provisioning completes
        try { await Task.Delay(TimeSpan.FromSeconds(45), ct); } catch { return; }
        await TryCheckAsync(identity, manual: false, ct);

        var timer = new PeriodicTimer(GatewayConstants.UpdateCheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                await TryCheckAsync(identity, manual: false, ct);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Out-of-band check triggered by a pushed UPDATE_GATEWAY command.</summary>
    public async Task<(bool ok, string detail)> CheckNowAsync(
        Func<(string bearer, string mid)?> identity, CancellationToken ct)
    {
        var r = await TryCheckAsync(identity, manual: true, ct);
        return r;
    }

    private async Task<(bool ok, string detail)> TryCheckAsync(
        Func<(string bearer, string mid)?> identity, bool manual, CancellationToken ct)
    {
        var id = identity();
        if (id is null) return (false, "Not provisioned.");

        var (ok, manifest, error) = await _server.GetUpdateManifestAsync(id.Value.bearer, id.Value.mid, ct);
        if (!ok) return (false, $"Manifest fetch failed: {error}");
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version))
            return (true, "No build published.");

        if (!IsNewer(manifest.Version, GatewayConstants.GatewayBuild))
        {
            var msg = $"Up to date (have {GatewayConstants.GatewayBuild}, manifest {manifest.Version}).";
            if (manual) _log.LogInformation("UpdateService: {Msg}", msg);
            return (true, msg);
        }

        // Loop-protection: did we already try this exact version and it
        // didn't take? If so, don't auto-retry (manual push overrides).
        if (!manual && AlreadyAttempted(manifest.Version))
        {
            _log.LogWarning("UpdateService: {V} was already attempted and did not take; " +
                "skipping auto-retry. Push UPDATE_GATEWAY to force.", manifest.Version);
            return (false, $"{manifest.Version} already attempted; awaiting newer build or manual push.");
        }

        _log.LogInformation("UpdateService: newer build {New} available (have {Cur}); downloading.",
            manifest.Version, GatewayConstants.GatewayBuild);
        _status.Update(s => s.Error = $"Updating to {manifest.Version}...");

        // ---- download ----
        Directory.CreateDirectory(GatewayConstants.UpdateStagingDir);
        var msiPath = Path.Combine(GatewayConstants.UpdateStagingDir,
            $"Nexus-Gateway-{Sanitize(manifest.Version)}.msi");
        var (dlOk, dlErr) = await _server.DownloadUpdateAsync(
            id.Value.bearer, id.Value.mid, manifest, msiPath, ct);
        if (!dlOk) return (false, $"Download failed: {dlErr}");

        // ---- verify SHA256 ----
        var actual = await Sha256FileAsync(msiPath, ct);
        if (!string.IsNullOrWhiteSpace(manifest.Sha256) &&
            !actual.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(msiPath); } catch { }
            _log.LogError("UpdateService: SHA256 mismatch for {V} (expected {Exp}, got {Got}); aborting.",
                manifest.Version, manifest.Sha256, actual);
            return (false, "SHA256 mismatch; download discarded.");
        }

        // record attempt BEFORE launching installer (loop-protection)
        RecordAttempt(manifest.Version);

        // ---- detached self-install ----
        try
        {
            LaunchInstaller(msiPath, manifest.Version);
            _log.LogInformation("UpdateService: installer launched for {V}; service will be replaced shortly.",
                manifest.Version);
            return (true, $"Installing {manifest.Version}.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "UpdateService: failed to launch installer for {V}.", manifest.Version);
            return (false, $"Installer launch failed: {ex.Message}");
        }
    }

    // ---- version comparison: GW.MMDD.YY-X ----
    // Newer if: later (yy,mm,dd) date, OR same date with a later letter.
    internal static bool IsNewer(string candidate, string current)
    {
        if (!TryParse(candidate, out var c)) return false;
        if (!TryParse(current, out var cur)) return true;   // unknown current -> accept
        if (c.date != cur.date) return c.date > cur.date;
        return c.letter > cur.letter;
    }

    private static bool TryParse(string v, out (int date, char letter) parsed)
    {
        parsed = (0, ' ');
        // GW.MMDD.YY-X
        try
        {
            var body = v.StartsWith("GW.", StringComparison.OrdinalIgnoreCase) ? v.Substring(3) : v;
            var dash = body.LastIndexOf('-');
            if (dash < 0 || dash == body.Length - 1) return false;
            var letter = char.ToUpperInvariant(body[dash + 1]);
            var datePart = body.Substring(0, dash);            // MMDD.YY
            var dot = datePart.IndexOf('.');
            if (dot < 0) return false;
            var mmdd = datePart.Substring(0, dot);             // MMDD
            var yy = datePart.Substring(dot + 1);              // YY
            if (mmdd.Length != 4 || yy.Length != 2) return false;
            var mm = int.Parse(mmdd.Substring(0, 2), CultureInfo.InvariantCulture);
            var dd = int.Parse(mmdd.Substring(2, 2), CultureInfo.InvariantCulture);
            var y = int.Parse(yy, CultureInfo.InvariantCulture);
            // sortable yyyymmdd
            var n = (2000 + y) * 10000 + mm * 100 + dd;
            parsed = (n, letter);
            return true;
        }
        catch { return false; }
    }

    // ---- loop-protection state ----
    private sealed class Attempt { public string Version { get; set; } = ""; public string Utc { get; set; } = ""; }

    private bool AlreadyAttempted(string version)
    {
        try
        {
            if (!File.Exists(GatewayConstants.UpdateAttemptMarker)) return false;
            var a = JsonSerializer.Deserialize<Attempt>(File.ReadAllText(GatewayConstants.UpdateAttemptMarker));
            return a != null && a.Version.Equals(version, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private void RecordAttempt(string version)
    {
        try
        {
            Directory.CreateDirectory(GatewayConstants.UpdateStagingDir);
            File.WriteAllText(GatewayConstants.UpdateAttemptMarker,
                JsonSerializer.Serialize(new Attempt { Version = version, Utc = DateTime.UtcNow.ToString("o") }));
        }
        catch (Exception ex) { _log.LogWarning(ex, "UpdateService: could not record attempt marker."); }
    }

    // ---- detached installer ----
    // Writes a .cmd that waits, runs msiexec (which stops/replaces/starts
    // the service via MajorUpgrade), then cleans the staged MSI. Launched
    // with UseShellExecute so it survives this service being stopped.
    private void LaunchInstaller(string msiPath, string version)
    {
        var logPath = Path.Combine(GatewayConstants.UpdateStagingDir, "install.log");
        var cmdPath = Path.Combine(GatewayConstants.UpdateStagingDir, "apply-update.cmd");
        var cmd = new StringBuilder();
        cmd.AppendLine("@echo off");
        cmd.AppendLine("rem Nexus Gateway self-update helper (detached).");
        cmd.AppendLine("timeout /t 5 /nobreak > nul");
        cmd.AppendLine($"msiexec /i \"{msiPath}\" /qn /norestart /l*v \"{logPath}\"");
        // give the service a moment to come back, then drop the staged MSI
        cmd.AppendLine("timeout /t 10 /nobreak > nul");
        cmd.AppendLine($"del /q \"{msiPath}\"");
        File.WriteAllText(cmdPath, cmd.ToString());

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{cmdPath}\"",
            UseShellExecute = true,          // detach from this process
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        Process.Start(psi);
    }

    // ---- helpers ----
    private static string Sanitize(string v)
    {
        var sb = new StringBuilder(v.Length);
        foreach (var ch in v) sb.Append(char.IsLetterOrDigit(ch) || ch is '.' or '-' ? ch : '_');
        return sb.ToString();
    }

    private static async Task<string> Sha256FileAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, ct);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
