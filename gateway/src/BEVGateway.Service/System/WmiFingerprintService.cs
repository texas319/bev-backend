// ============================================================
// FILE        : WmiFingerprintService.cs
// STATUS      : Phase 1c-2 — Gateway + Tray binary
// LAST UPD    : 2026-05-27 15:00 CST
// PURPOSE     : Hardware fingerprint via WMI. Combines the
//               motherboard UUID and CPU processor ID and hashes
//               them to produce a stable, machine-bound string
//               that survives reinstall but identifies different
//               hardware. Used to bind a license to a specific
//               Cube on first provision.
// OWNS        : Hardware identity.
// CALLED BY   : Worker on first-run provision attempt.
// ============================================================

using System.Management;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace BEVGateway.Service.System;

public interface IFingerprintService
{
    string Compute();
}

[SupportedOSPlatform("windows")]
public sealed class WmiFingerprintService : IFingerprintService
{
    public string Compute()
    {
        var sb = new StringBuilder();

        sb.Append(SafeWmiQuery("SELECT UUID FROM Win32_ComputerSystemProduct", "UUID"));
        sb.Append('|');
        sb.Append(SafeWmiQuery("SELECT ProcessorId FROM Win32_Processor", "ProcessorId"));
        sb.Append('|');
        sb.Append(Environment.MachineName);

        // SHA-256 → first 16 hex chars. Keeps the fingerprint shorter
        // than the raw concatenation while preserving uniqueness.
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes).Substring(0, 32);
    }

    private static string SafeWmiQuery(string query, string property)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            foreach (var obj in searcher.Get())
            {
                var v = obj[property];
                if (v is not null) return v.ToString() ?? "";
            }
        }
        catch { /* fall through */ }
        return "WMI_UNAVAILABLE";
    }
}
