// ============================================================
// FILE        : WireModels.cs
// STATUS      : Phase 1c-2 — Gateway + Tray binary
// LAST UPD    : 2026-05-27 15:00 CST
// PURPOSE     : Wire format DTOs for Server + Hive HTTP calls.
//               Field names match the deployed backends exactly.
//               If these drift from server-side, integration
//               silently breaks — keep in lockstep.
// OWNS        : Wire contracts.
// CALLED BY   : ServerClient, HiveClient.
// ============================================================

using System.Text.Json.Serialization;

namespace BEVGateway.Shared.Wire;

// -------- Server /v1/provision --------

public sealed class ProvisionRequest
{
    [JsonPropertyName("email")]         public string Email        { get; set; } = "";
    [JsonPropertyName("license_key")]   public string LicenseKey   { get; set; } = "";
    [JsonPropertyName("fingerprint")]   public string Fingerprint  { get; set; } = "";
    [JsonPropertyName("gateway_build")] public string GatewayBuild { get; set; } = "";
}

public sealed class ProvisionResponse
{
    [JsonPropertyName("ok")]              public bool    Ok            { get; set; }
    [JsonPropertyName("tenant_id")]       public string? TenantId      { get; set; }
    [JsonPropertyName("machine_id")]      public string? MachineId     { get; set; }
    [JsonPropertyName("tier")]            public string? Tier          { get; set; }
    [JsonPropertyName("dragon_tier_max")] public int?    DragonTierMax { get; set; }
    [JsonPropertyName("bound_utc")]       public string? BoundUtc      { get; set; }
    [JsonPropertyName("bearer_token")]    public string? BearerToken   { get; set; }
    [JsonPropertyName("expires_utc")]     public string? ExpiresUtc    { get; set; }
    [JsonPropertyName("error")]           public string? Error         { get; set; }
}

// -------- Server /v1/tenant/mids --------

public sealed class CubeRegisterRequest
{
    [JsonPropertyName("mid")]            public string Mid          { get; set; } = "";
    [JsonPropertyName("hostname")]       public string Hostname     { get; set; } = "";
    [JsonPropertyName("first_seen_utc")] public string FirstSeenUtc { get; set; } = "";
    [JsonPropertyName("operator_email")] public string OperatorEmail { get; set; } = "";
}

public sealed class CubeRegisterResponse
{
    [JsonPropertyName("registered")] public bool    Registered { get; set; }
    [JsonPropertyName("tenant_id")]  public string? TenantId   { get; set; }
    [JsonPropertyName("fleet_role")] public string? FleetRole  { get; set; }
    [JsonPropertyName("error")]      public string? Error      { get; set; }
}

// -------- Hive /v1/hud-snapshot --------

public sealed class HudStrategy
{
    [JsonPropertyName("family")]  public string  Family { get; set; } = "";
    [JsonPropertyName("armed")]   public bool    Armed  { get; set; }
    [JsonPropertyName("pos")]     public int     Pos    { get; set; }
    [JsonPropertyName("pnl_usd")] public decimal PnlUsd { get; set; }
}

public sealed class HudAmc
{
    [JsonPropertyName("leader_account")]    public string  LeaderAccount    { get; set; } = "";
    [JsonPropertyName("follower_count")]    public int     FollowerCount    { get; set; }
    [JsonPropertyName("replication_state")] public string  ReplicationState { get; set; } = "ready";
    [JsonPropertyName("daily_pnl_usd")]     public decimal DailyPnlUsd      { get; set; }
    [JsonPropertyName("open_positions")]    public int     OpenPositions    { get; set; }
}

public sealed class HudFeeds
{
    [JsonPropertyName("l1")]         public bool L1        { get; set; }
    [JsonPropertyName("l2")]         public bool L2        { get; set; }
    [JsonPropertyName("hive_seven")] public bool HiveSeven { get; set; }
}

public sealed class HudAuditTail
{
    [JsonPropertyName("tca_last_row_utc")]     public string? TcaLastRowUtc     { get; set; }
    [JsonPropertyName("barsnap_last_row_utc")] public string? BarsnapLastRowUtc { get; set; }
    [JsonPropertyName("sigeval_last_row_utc")] public string? SigevalLastRowUtc { get; set; }
}

public sealed class HudSnapshotRequest
{
    [JsonPropertyName("mid")]          public string         Mid         { get; set; } = "";
    [JsonPropertyName("wall_utc")]     public string         WallUtc     { get; set; } = "";
    [JsonPropertyName("build_label")]  public string         BuildLabel  { get; set; } = "";
    [JsonPropertyName("nt8_state")]    public string         Nt8State    { get; set; } = "unknown";
    [JsonPropertyName("nexus_loaded")] public bool           NexusLoaded { get; set; }
    [JsonPropertyName("strategies")]   public List<HudStrategy>? Strategies { get; set; }
    [JsonPropertyName("amc")]          public HudAmc?        Amc         { get; set; }
    [JsonPropertyName("feeds")]        public HudFeeds?      Feeds       { get; set; }
    [JsonPropertyName("audit_tail")]   public HudAuditTail?  AuditTail   { get; set; }
}

public sealed class HudSnapshotResponse
{
    [JsonPropertyName("received_utc")]     public string ReceivedUtc     { get; set; } = "";
    [JsonPropertyName("next_poll_sec")]    public int    NextPollSec     { get; set; }
    [JsonPropertyName("rulebook_version")] public string RulebookVersion { get; set; } = "";
}

// -------- Hive /v1/commands --------

public sealed class GatewayCommand
{
    [JsonPropertyName("command_id")]  public string CommandId  { get; set; } = "";
    [JsonPropertyName("issued_utc")]  public string IssuedUtc  { get; set; } = "";
    [JsonPropertyName("kind")]        public string Kind       { get; set; } = "";
    [JsonPropertyName("args")]        public Dictionary<string, object>? Args { get; set; }
    [JsonPropertyName("expires_utc")] public string ExpiresUtc { get; set; } = "";
}

public sealed class CommandsResponse
{
    [JsonPropertyName("commands")]       public List<GatewayCommand>? Commands { get; set; }
    [JsonPropertyName("next_since_utc")] public string                NextSinceUtc { get; set; } = "";
}

// -------- Hive /v1/command-ack --------

public sealed class CommandAckRequest
{
    [JsonPropertyName("command_id")]   public string  CommandId   { get; set; } = "";
    [JsonPropertyName("result")]       public string  Result      { get; set; } = "";
    [JsonPropertyName("detail")]       public string? Detail      { get; set; }
    [JsonPropertyName("executed_utc")] public string  ExecutedUtc { get; set; } = "";
}

public sealed class CommandAckResponse
{
    [JsonPropertyName("received_utc")] public string ReceivedUtc { get; set; } = "";
}

// ============================================================
// AUDIT SHIPPER (Gateway -> Hive /v1/audit/ingest)
// ============================================================

// One CSV per request: the filename (Hive classifies + parses by it)
// and the raw file text. Tenant + MID come from the Bearer JWT.
public sealed class AuditIngestRequest
{
    [JsonPropertyName("file_name")] public string FileName { get; set; } = "";
    [JsonPropertyName("content")]   public string Content  { get; set; } = "";
}

public sealed class AuditIngestResponse
{
    [JsonPropertyName("status")]        public string Status       { get; set; } = "";  // OK | DUPLICATE
    [JsonPropertyName("log_type")]      public string? LogType     { get; set; }
    [JsonPropertyName("rows_parsed")]   public int    RowsParsed   { get; set; }
    [JsonPropertyName("rows_inserted")] public int    RowsInserted { get; set; }
}

// ============================================================
// GATEWAY AUTO-UPDATE (Gateway <- Server)
// ============================================================

// Server's manifest of the latest published Gateway build.
// The Gateway compares Version to its own GatewayConstants.GatewayBuild;
// if newer, it downloads the MSI, verifies Sha256, and self-installs.
public sealed class GatewayUpdateManifest
{
    [JsonPropertyName("version")]      public string Version     { get; set; } = "";  // e.g. "GW.0602.26-N"
    [JsonPropertyName("msi_size")]     public long   MsiSize     { get; set; }
    [JsonPropertyName("sha256")]       public string Sha256      { get; set; } = "";  // hex, lowercase
    [JsonPropertyName("mandatory")]    public bool   Mandatory   { get; set; }        // auto-install w/o prompt
    [JsonPropertyName("notes")]        public string? Notes      { get; set; }
    [JsonPropertyName("download_url")] public string? DownloadUrl { get; set; }        // optional direct URL; else use /v1/gateway/download
    [JsonPropertyName("published_utc")]public string? PublishedUtc{ get; set; }
}
