// ============================================================
// FILE        : IdentityStore.cs
// STATUS      : Phase 1c-2 — Gateway + Tray binary
// LAST UPD    : 2026-05-27 15:00 CST
// PURPOSE     : Reads/writes the encrypted PrivateIdentity at
//               C:\ProgramData\BEVGateway\identity.json (DPAPI
//               machine scope). Mirrors a plaintext subset
//               (PublicIdentity) into the operator's Documents
//               NEXUS drop folder so NEXUS knows its tenant + MID
//               without needing the JWT.
//               First-run wizard creates a PendingProvision file
//               separately (see SetupWizard) — Worker watches
//               for that and runs the provision.
// OWNS        : Identity persistence on disk.
// CALLED BY   : Worker, IPC server (for status), setup wizard.
// ============================================================

using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BEVGateway.Shared;
using Microsoft.Extensions.Logging;

namespace BEVGateway.Service.Storage;

public interface IIdentityStore
{
    bool Exists();
    Task<PrivateIdentity?> LoadAsync(CancellationToken ct);
    Task SaveAsync(PrivateIdentity identity, CancellationToken ct);
    Task WriteNexusDropAsync(PrivateIdentity identity, CancellationToken ct);
    Task<PendingProvision?> ReadPendingProvisionAsync(CancellationToken ct);
    Task ClearPendingProvisionAsync(CancellationToken ct);
    string PendingProvisionPath { get; }
}

public sealed class IdentityStore : IIdentityStore
{
    private readonly ILogger<IdentityStore> _log;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public string PendingProvisionPath => GatewayConstants.PendingProvisionPath;

    public IdentityStore(ILogger<IdentityStore> log) { _log = log; }

    public bool Exists() => File.Exists(GatewayConstants.IdentityPath);

    [SupportedOSPlatformGuard("windows")]
    public async Task<PrivateIdentity?> LoadAsync(CancellationToken ct)
    {
        if (!Exists()) return null;
        try
        {
            var encrypted = await File.ReadAllBytesAsync(GatewayConstants.IdentityPath, ct);
            byte[] plain;
            if (OperatingSystem.IsWindows())
            {
                plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.LocalMachine);
            }
            else
            {
                plain = encrypted; // dev path on non-Windows
            }
            var json = Encoding.UTF8.GetString(plain);
            return JsonSerializer.Deserialize<PrivateIdentity>(json);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load identity from {Path}", GatewayConstants.IdentityPath);
            return null;
        }
    }

    [SupportedOSPlatformGuard("windows")]
    private static readonly SemaphoreSlim _saveLock = new(1, 1);

    public async Task SaveAsync(PrivateIdentity identity, CancellationToken ct)
    {
        // Serialize concurrent saves. The reprovision loop could fire
        // two saves at once, and both raced on identity.json.tmp -
        // the second threw "file in use by another process" and the
        // unhandled exception crashed the whole service. A lock plus
        // a unique tmp name per write makes this safe regardless of
        // how many callers overlap.
        await _saveLock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(GatewayConstants.IdentityDir);
            var json = JsonSerializer.Serialize(identity, JsonOpts);
            var plain = Encoding.UTF8.GetBytes(json);
            byte[] encrypted;
            if (OperatingSystem.IsWindows())
            {
                encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.LocalMachine);
            }
            else
            {
                encrypted = plain; // dev path
            }
            // Atomic write with a UNIQUE tmp name (no fixed .tmp to collide on)
            var tmp = GatewayConstants.IdentityPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(tmp, encrypted, ct);
            File.Move(tmp, GatewayConstants.IdentityPath, overwrite: true);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public async Task WriteNexusDropAsync(PrivateIdentity identity, CancellationToken ct)
    {
        // Drop into the Documents folder of every user that has the
        // NEXUS folder structure. In practice this is the operator's
        // own user. Since the service runs as LocalSystem we walk
        // C:\Users\* looking for the NinjaTrader 8\BEV folder.
        var publicId = new PublicIdentity
        {
            TenantId   = identity.TenantId,
            MachineId  = identity.MachineId,
            NodeClass  = identity.NodeClass,
            Tier       = identity.Tier,
            FleetRole  = identity.FleetRole,
            BoundUtc   = identity.BoundUtc,
            CubeTag    = GatewayConstants.ReadCubeTag(),
            WrittenUtc = DateTime.UtcNow.ToString("o")
        };
        var json = JsonSerializer.Serialize(publicId, JsonOpts);

        var users = new DirectoryInfo(@"C:\Users");
        if (!users.Exists) return;

        foreach (var u in users.GetDirectories())
        {
            try
            {
                var bevDir = Path.Combine(u.FullName, "Documents",
                    "NinjaTrader 8", "BEV", "Gateway");
                Directory.CreateDirectory(bevDir);
                var file = Path.Combine(bevDir, GatewayConstants.NexusDropFileName);
                await File.WriteAllTextAsync(file, json, ct);
            }
            catch
            {
                // Skip user dirs we can't write to (system accounts,
                // permission issues). Best-effort fan-out.
            }
        }
    }

    public async Task<PendingProvision?> ReadPendingProvisionAsync(CancellationToken ct)
    {
        var path = PendingProvisionPath;
        if (!File.Exists(path)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            return JsonSerializer.Deserialize<PendingProvision>(json);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to parse pending provision file.");
            return null;
        }
    }

    public Task ClearPendingProvisionAsync(CancellationToken ct)
    {
        try { File.Delete(PendingProvisionPath); } catch { }
        return Task.CompletedTask;
    }
}
