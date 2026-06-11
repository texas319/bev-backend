// ============================================================
// FILE        : SystemActions.cs
// STATUS      : Phase 1c-2 — Gateway + Tray binary
// LAST UPD    : 2026-05-27 15:00 CST
// PURPOSE     : Command implementations that affect the local
//               machine. RESTART_NT8 / REBOOT_BOX / KILL_ALL are
//               wired here but only PING + REFRESH_CREDENTIALS
//               are exercised in Sprint 1.
// OWNS        : Local side effects of downstream commands.
// CALLED BY   : Worker after pulling a command from Hive.
// ============================================================

using System.Diagnostics;
using System.Runtime.Versioning;
using BEVGateway.Shared;
using Microsoft.Extensions.Logging;

namespace BEVGateway.Service.System;

public interface ISystemActions
{
    Task<(bool ok, string detail)> RestartNt8Async(CancellationToken ct);
    Task<(bool ok, string detail)> RebootBoxAsync(CancellationToken ct);
    Task<(bool ok, string detail)> WriteKillAllFlagAsync(CancellationToken ct);
    Task<(bool ok, string detail)> WriteInvokeAsync(string functionId, string argsJson, string actor, string requestId, CancellationToken ct);
    void OpenLogDirectory();
}

public sealed class SystemActions : ISystemActions
{
    private readonly ILogger<SystemActions> _log;

    public SystemActions(ILogger<SystemActions> log) { _log = log; }

    public async Task<(bool ok, string detail)> RestartNt8Async(CancellationToken ct)
    {
        try
        {
            var procs = Process.GetProcessesByName("NinjaTrader");
            foreach (var p in procs)
            {
                try { p.Kill(entireProcessTree: true); p.WaitForExit(15000); }
                catch (Exception ex) { _log.LogWarning(ex, "Failed to kill NinjaTrader pid {Pid}", p.Id); }
            }
            await Task.Delay(2000, ct);

            // Default install path; not always correct. Real install lookup
            // can come later — for now we just signal "stopped" and let the
            // operator/Tradeify/Bulenox autostart bring it back.
            var nt8 = @"C:\Program Files (x86)\NinjaTrader 8\bin\NinjaTrader.exe";
            if (File.Exists(nt8))
            {
                var psi = new ProcessStartInfo(nt8) { UseShellExecute = true };
                Process.Start(psi);
                return (true, "NinjaTrader restarted.");
            }
            return (true, "NinjaTrader stopped; relaunch handled by host autostart.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    [SupportedOSPlatformGuard("windows")]
    public Task<(bool ok, string detail)> RebootBoxAsync(CancellationToken ct)
    {
        try
        {
            // 30-second delay gives the Gateway time to ack BEFORE going down.
            var psi = new ProcessStartInfo("shutdown", "/r /t 30 /c \"BEV Gateway: scheduled reboot from Hive command.\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            Process.Start(psi);
            return Task.FromResult((true, "Reboot scheduled in 30 seconds."));
        }
        catch (Exception ex)
        {
            return Task.FromResult((false, ex.Message));
        }
    }

    public async Task<(bool ok, string detail)> WriteKillAllFlagAsync(CancellationToken ct)
    {
        try
        {
            // BEV root convention — Documents\NinjaTrader 8\BEV
            var bevRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "NinjaTrader 8", "BEV");
            Directory.CreateDirectory(bevRoot);
            var flagPath = Path.Combine(bevRoot, "DISABLE.flag");
            await File.WriteAllTextAsync(flagPath,
                $"Set by Gateway KILL_ALL at {DateTime.UtcNow:o}\n", ct);
            return (true, $"DISABLE.flag written at {flagPath}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<(bool ok, string detail)> WriteInvokeAsync(string functionId, string argsJson, string actor, string requestId, CancellationToken ct)
    {
        // Hand a control-plane function to EAGLE via the relay command file.
        // EAGLE (PART 36/37) polls command.json and executes the function_id
        // against its own accounts/instances. We write one command object;
        // the request_id lets the result be correlated back through the
        // audit log. actor is the dashboard email (server-stamped upstream).
        try
        {
            if (string.IsNullOrWhiteSpace(argsJson)) argsJson = "{}";
            var payload =
                "{\"request_id\":\"" + Esc(requestId) + "\"," +
                "\"function_id\":\"" + Esc(functionId) + "\"," +
                "\"actor\":\"" + Esc(actor) + "\"," +
                "\"ts_utc\":\"" + DateTime.UtcNow.ToString("o") + "\"," +
                "\"args\":" + argsJson + "}";

            var targets = new List<string>();
            // every per-box Relay dir (service may not be the trading user)
            foreach (var d in GatewayConstants.DiscoverRelayDirs()) targets.Add(d);
            // plus the current user's Relay dir as a fallback
            var mine = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "NinjaTrader 8", "BEV", "Relay");
            if (!targets.Contains(mine)) targets.Add(mine);

            int written = 0;
            foreach (var dir in targets)
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    var path = Path.Combine(dir, "command.json");
                    // write atomically: temp then move, so EAGLE never reads a half file
                    var tmp = path + ".tmp";
                    await File.WriteAllTextAsync(tmp, payload, ct);
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(tmp, path);
                    written++;
                }
                catch (Exception ex) { _log.LogWarning(ex, "INVOKE write failed for {Dir}", dir); }
            }
            return written > 0
                ? (true, $"INVOKE {functionId} written to {written} relay dir(s) (req {requestId}).")
                : (false, "No relay dir writable.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

    public void OpenLogDirectory()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = GatewayConstants.LogDir,
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "OpenLogDirectory failed");
        }
    }
}
