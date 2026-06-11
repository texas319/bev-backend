// ============================================================
// FILE        : GatewayConstants.cs
// STATUS      : Phase 1c-2 — NEXUS visual retrofit + rename
// LAST UPD    : 2026-05-28 01:00 CST
// PURPOSE     : Compile-time constants — endpoints, paths,
//               build label, brand strings. Single source of
//               truth shared between Service and Tray.
//               RENAMED this build: product is now "Nexus
//               Gateway" under the NEXUS family. BEV is the
//               strategy NEXUS consumes; the Gateway is NEXUS
//               infrastructure.
// OWNS        : Platform configuration + brand identity.
// CALLED BY   : Everything.
// ============================================================

namespace BEVGateway.Shared;

public static class GatewayConstants
{
    public const string GatewayBuild = "GW.0610.26-A";

    // ---- Brand ----
    public const string ProductName   = "Nexus Gateway";
    public const string ProductFamily = "NEXUS";
    public const string BadgeText     = "GATEWAY";  // NEX-style badge (3-4 chars, caps)
    public const string Manufacturer  = "BEV Systems";

    // Node class is determined locally from the CPU (see
    // NodeClassDetector): server-grade Xeon silicon => CUBE,
    // consumer i-series / AMD => SPHERE. Unknown => CUBE (safe default).
    public const string NodeClassCube   = "CUBE";
    public const string NodeClassSphere = "SPHERE";

    // Live backend endpoints (custom domains, TLS).
    public const string ServerBaseUrl = "https://server.bevcloud.app";
    public const string HiveBaseUrl   = "https://hive.bevcloud.app";

    // Per-machine identity (DPAPI-encrypted, machine scope).
    public const string IdentityDir   = @"C:\ProgramData\NexusGateway";
    public const string IdentityFile  = "identity.json";
    public const string IdentityPath  = IdentityDir + @"\" + IdentityFile;
    public const string PendingProvisionFile = "pending-provision.json";

    // Per-user NEXUS-readable identity drop (plaintext subset).
    // NEXUS reads this from the NinjaTrader user session to learn its
    // own MID + tenant. The path stays under "BEV\Gateway" because
    // that is the contract NEXUS already reads — do NOT rename without
    // coordinating with the NEXUS team.
    public const string NexusDropFileName = "cube-identity.json";
    public const string NexusDropRelPath  =
        @"NinjaTrader 8\BEV\Gateway\cube-identity.json";

    // Service log directory.
    public const string LogDir = IdentityDir + @"\logs";

    // Named pipe for Tray <-> Service IPC.
    public const string TrayPipeName = "NexusGateway.tray";

    // Service name as registered with Windows.
    public const string ServiceName        = "NexusGateway";
    public const string ServiceDisplayName = "Nexus Gateway";
    public const string ServiceDescription =
        "Nexus Gateway - telemetry + command relay between this Cube and the NEXUS platform.";

    // Cadences.
    public static readonly TimeSpan HudInterval        = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan CommandPollMaxWait = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan JwtRefreshLead     = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan StartupRetryDelay  = TimeSpan.FromSeconds(10);

    // ---- Audit shipper ----
    // EAGLE writes audit CSVs to the OPERATOR's Documents folder:
    //   <user>\Documents\NinjaTrader 8\BEV\AUDIT LOG
    // The Gateway runs as a LocalSystem service, so %USERPROFILE%
    // resolves to C:\Windows\system32\config\systemprofile — the WRONG
    // place. Mirror the NEXUS-drop approach: scan C:\Users\* for the
    // real audit dir(s). Returns every existing audit dir across user
    // profiles (normally just the operator's).
    public const string AuditLogRelPath =
        @"Documents\NinjaTrader 8\BEV\AUDIT LOG";

    // BEV LiveLink (state out + commands in), per EAGLE LiveLink memo
    // 2026-06-04. EAGLE writes one HUD.<instance_id>.json per instance
    // (~1Hz) under the per-box Relay\FLEET dir; the Gateway tails the
    // glob and forwards each to Hive. Commands go back via command.json.
    public const string LiveLinkRelDir   = @"Documents\NinjaTrader 8\BEV\Relay\FLEET";
    public const string LiveLinkGlob     = "HUD.*.json";
    public const string CommandRelPath   = @"Documents\NinjaTrader 8\BEV\Relay\command.json";
    // CUBE TAG — a vanity VPS label (e.g. "VPS_W1") shown next to the MID
    // in the terminal header (C-XXXXXX // TAG). Stored in a small local
    // file in ProgramData, NOT a database row — the MID is the serial
    // (identity); the tag is just a friendly name. Set at install or in
    // the running service. Empty = no tag (terminal shows MID alone).
    public static string CubeTagPath =>
        global::System.IO.Path.Combine(
            global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.CommonApplicationData),
            "NexusGateway", "cube-tag.txt");
    public static string ReadCubeTag()
    {
        try
        {
            var p = CubeTagPath;
            if (global::System.IO.File.Exists(p))
                return global::System.IO.File.ReadAllText(p).Trim();
        }
        catch { }
        return "";
    }
    public static void WriteCubeTag(string tag)
    {
        try
        {
            var p = CubeTagPath;
            global::System.IO.Directory.CreateDirectory(global::System.IO.Path.GetDirectoryName(p)!);
            global::System.IO.File.WriteAllText(p, (tag ?? "").Trim());
        }
        catch { }
    }
    // Relay dir (parent of FLEET) per box — where command.json is written
    // for EAGLE to poll. Discovered the same way as the LiveLink dirs.
    public const string RelayRelDir      = @"Documents\NinjaTrader 8\BEV\Relay";
    public static global::System.Collections.Generic.IEnumerable<string> DiscoverRelayDirs()
    {
        var usersRoot = new global::System.IO.DirectoryInfo(@"C:\Users");
        if (!usersRoot.Exists) yield break;
        foreach (var u in usersRoot.GetDirectories())
        {
            var n = u.Name;
            if (n.Equals("Public", global::System.StringComparison.OrdinalIgnoreCase) ||
                n.Equals("Default", global::System.StringComparison.OrdinalIgnoreCase) ||
                n.Equals("Default User", global::System.StringComparison.OrdinalIgnoreCase) ||
                n.Equals("All Users", global::System.StringComparison.OrdinalIgnoreCase))
                continue;
            string dir;
            try { dir = global::System.IO.Path.Combine(u.FullName, RelayRelDir); }
            catch { continue; }
            if (global::System.IO.Directory.Exists(dir))
                yield return dir;
        }
    }
    // How often the Gateway reads + forwards live snapshots.
    public static readonly TimeSpan LiveLinkInterval = TimeSpan.FromSeconds(15);
    // A snapshot whose timestamp is older than this is treated stale.
    public static readonly TimeSpan LiveLinkStaleAfter = TimeSpan.FromSeconds(60);

    // Discover per-box LiveLink FLEET dirs the same way as audit dirs.
    public static IEnumerable<string> DiscoverLiveLinkDirs()
    {
        var usersRoot = new global::System.IO.DirectoryInfo(@"C:\Users");
        if (!usersRoot.Exists) yield break;
        foreach (var u in usersRoot.GetDirectories())
        {
            var n = u.Name;
            if (n.Equals("Public", StringComparison.OrdinalIgnoreCase) ||
                n.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
                n.Equals("Default User", StringComparison.OrdinalIgnoreCase) ||
                n.Equals("All Users", StringComparison.OrdinalIgnoreCase))
                continue;
            string dir;
            try { dir = global::System.IO.Path.Combine(u.FullName, LiveLinkRelDir); }
            catch { continue; }
            if (global::System.IO.Directory.Exists(dir))
                yield return dir;
        }
    }

    // Resolve the command.json path for the box (first real user profile
    // that has the Relay tree). Returns null if none found.
    public static string? ResolveCommandPath()
    {
        var usersRoot = new global::System.IO.DirectoryInfo(@"C:\Users");
        if (!usersRoot.Exists) return null;
        foreach (var u in usersRoot.GetDirectories())
        {
            var n = u.Name;
            if (n.Equals("Public", StringComparison.OrdinalIgnoreCase) ||
                n.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
                n.Equals("Default User", StringComparison.OrdinalIgnoreCase) ||
                n.Equals("All Users", StringComparison.OrdinalIgnoreCase))
                continue;
            string fleetDir;
            try { fleetDir = global::System.IO.Path.Combine(u.FullName, LiveLinkRelDir); }
            catch { continue; }
            if (global::System.IO.Directory.Exists(fleetDir))
            {
                try { return global::System.IO.Path.Combine(u.FullName, CommandRelPath); }
                catch { continue; }
            }
        }
        return null;
    }
    public static IEnumerable<string> DiscoverAuditLogDirs()
    {
        var usersRoot = new global::System.IO.DirectoryInfo(@"C:\Users");
        if (!usersRoot.Exists) yield break;
        foreach (var u in usersRoot.GetDirectories())
        {
            // Skip obvious non-interactive/system profiles.
            var n = u.Name;
            if (n.Equals("Public", StringComparison.OrdinalIgnoreCase) ||
                n.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
                n.Equals("Default User", StringComparison.OrdinalIgnoreCase) ||
                n.Equals("All Users", StringComparison.OrdinalIgnoreCase))
                continue;
            string dir;
            try { dir = global::System.IO.Path.Combine(u.FullName, AuditLogRelPath); }
            catch { continue; }
            if (global::System.IO.Directory.Exists(dir))
                yield return dir;
        }
    }
    // Recurse into dated subfolders (e.g. AUDIT LOG\BEV-EAGLE-06-02-26\).
    public const bool AuditRecurse = true;
    // How often to scan for new/changed files.
    public static readonly TimeSpan AuditScanInterval = TimeSpan.FromSeconds(30);
    // A file must be unmodified for this long before we ship it, so we
    // never POST a CSV EAGLE is still mid-write on (rotation safety).
    public static readonly TimeSpan AuditQuietPeriod  = TimeSpan.FromSeconds(20);
    // Local record of what we've already shipped (path|size|mtime|sha-short),
    // so a service restart doesn't re-POST everything. Hive dedups by content
    // hash regardless; this just saves the round-trips.
    public static string AuditShipStatePath => IdentityDir + @"\audit-ship-state.json";
    // Max files shipped per scan cycle (backpressure — don't flood Hive).
    public const int AuditMaxPerCycle = 25;

    // ---- Auto-update ----
    // The Gateway polls the Server manifest for a newer build and self-
    // installs. Check on startup + on this slow cadence. A pushed
    // UPDATE_GATEWAY command triggers an immediate check out of band.
    public static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(1);
    // Where the downloaded MSI + install helper are staged.
    public static string UpdateStagingDir => IdentityDir + @"\update";
    // Marker the updater writes before launching the installer, so on the
    // next service start we know an update was attempted (and to what
    // version) — for loop-protection if an install fails to take.
    public static string UpdateAttemptMarker => UpdateStagingDir + @"\last-attempt.json";

    // Pending-provision absolute path (used by both Tray writer + Service reader).
    public static string PendingProvisionPath => IdentityDir + @"\" + PendingProvisionFile;
}
