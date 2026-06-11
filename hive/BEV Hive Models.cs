// ============================================================
// FILE        : BEV Hive Models.cs
// STATUS      : Phase 1b — Hive /v1/seven/query stub
// LAST UPD    : 2026-05-24 13:00 CST
// PURPOSE     : Wire DTOs for /v1/seven/query. Field names match
//               the Hive Briefing Memo Rev 2 Section 2 contract
//               exactly. Drift here = silent contract break with
//               NEXUS / Gateway.
// OWNS        : SevenQueryRequest, SevenQueryResponse.
// CALLED BY   : SevenQueryFunction.
// CHANGE LOG  :
//   2026-05-24 13:00 CST  v0-26.0524-B  Initial scaffold (Phase 1b).
// ============================================================

using System.Text.Json.Serialization;

namespace BEV.Hive.Models;

public static class Modes
{
    public const string Eagle   = "Eagle";
    public const string Phoenix = "Phoenix";
    public const string Dragon  = "Dragon";

    public static bool IsValid(string m) =>
        m == Eagle || m == Phoenix || m == Dragon;
}

public sealed class PromptBlock
{
    [JsonPropertyName("instruction")]          public string  Instruction         { get; set; } = "";
    [JsonPropertyName("context_ref")]          public string? ContextRef          { get; set; }
    [JsonPropertyName("audit_window_minutes")] public int     AuditWindowMinutes  { get; set; }
}

public sealed class ClientBlock
{
    [JsonPropertyName("build")]       public string Build       { get; set; } = "";
    [JsonPropertyName("nexus_build")] public string NexusBuild  { get; set; } = "";
    [JsonPropertyName("schema")]      public string Schema      { get; set; } = "1";
}

public sealed class SevenQueryRequest
{
    [JsonPropertyName("request_id")]   public string       RequestId    { get; set; } = "";
    [JsonPropertyName("operator_mid")] public string       OperatorMid  { get; set; } = "";
    [JsonPropertyName("tenant_id")]    public string       TenantId     { get; set; } = "";
    [JsonPropertyName("mode")]         public string       Mode         { get; set; } = "";
    [JsonPropertyName("dragon_tier")]  public int?         DragonTier   { get; set; }
    [JsonPropertyName("prompt")]       public PromptBlock? Prompt       { get; set; }
    [JsonPropertyName("client")]       public ClientBlock? Client       { get; set; }
}

public sealed class UsageBlock
{
    [JsonPropertyName("input_tokens")]  public int    InputTokens  { get; set; }
    [JsonPropertyName("output_tokens")] public int    OutputTokens { get; set; }
    [JsonPropertyName("cycle_id")]      public string CycleId      { get; set; } = "";
}

public sealed class StructuredBlock
{
    [JsonPropertyName("type")] public string                       Type { get; set; } = "";
    [JsonPropertyName("json")] public Dictionary<string, object>? Json { get; set; }
}

public sealed class SevenQueryResponse
{
    [JsonPropertyName("request_id")]        public string                RequestId        { get; set; } = "";
    [JsonPropertyName("response_text")]     public string                ResponseText     { get; set; } = "";
    [JsonPropertyName("structured_blocks")] public List<StructuredBlock> StructuredBlocks { get; set; } = new();
    [JsonPropertyName("usage")]             public UsageBlock            Usage            { get; set; } = new();
    [JsonPropertyName("source")]            public string                Source           { get; set; } = "dev_fallback";
}

public sealed class ErrorResponse
{
    [JsonPropertyName("error")]   public string  Error   { get; set; } = "";
    [JsonPropertyName("message")] public string? Message { get; set; }
}

// ============================================================
// HUD SNAPSHOT — POST /v1/hud-snapshot
// ============================================================

public sealed class HudStrategy
{
    [JsonPropertyName("family")]  public string  Family  { get; set; } = "";
    [JsonPropertyName("armed")]   public bool    Armed   { get; set; }
    [JsonPropertyName("pos")]     public int     Pos     { get; set; }
    [JsonPropertyName("pnl_usd")] public decimal PnlUsd  { get; set; }
}

public sealed class HudAmc
{
    [JsonPropertyName("leader_account")]    public string  LeaderAccount    { get; set; } = "";
    [JsonPropertyName("follower_count")]    public int     FollowerCount    { get; set; }
    [JsonPropertyName("replication_state")] public string  ReplicationState { get; set; } = "";
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
    [JsonPropertyName("received_utc")]     public string  ReceivedUtc     { get; set; } = "";
    [JsonPropertyName("next_poll_sec")]    public int     NextPollSec     { get; set; }
    [JsonPropertyName("rulebook_version")] public string  RulebookVersion { get; set; } = "";
}

// Stored in Cosmos container `telemetry`, partition key /tenantId.
// One row per HUD snapshot received. TTL of ~24h keeps storage bounded
// while preserving recent fleet-wide state for support queries.
public sealed class HudSnapshotDoc
{
    [JsonPropertyName("id")]          public string  Id          { get; set; } = "";
    [JsonPropertyName("tenantId")]    public string  TenantId    { get; set; } = "";
    [JsonPropertyName("machineId")]   public string  MachineId   { get; set; } = "";
    [JsonPropertyName("receivedUtc")] public string  ReceivedUtc { get; set; } = "";
    [JsonPropertyName("wallUtc")]     public string  WallUtc     { get; set; } = "";
    [JsonPropertyName("buildLabel")]  public string  BuildLabel  { get; set; } = "";
    [JsonPropertyName("payload")]     public HudSnapshotRequest? Payload { get; set; }
    [JsonPropertyName("docType")]     public string  DocType     { get; set; } = "hud_snapshot";
    [JsonPropertyName("ttl")]         public int     Ttl         { get; set; } = 86400;  // 24h
}

// ============================================================
// COMMANDS — GET /v1/commands  +  POST /v1/command-ack
// ============================================================

public static class CommandKinds
{
    public const string Ping               = "PING";
    public const string RestartNt8         = "RESTART_NT8";
    public const string RebootBox          = "REBOOT_BOX";
    public const string KillAll            = "KILL_ALL";
    public const string RefreshCredentials = "REFRESH_CREDENTIALS";

    public static bool IsValid(string k) =>
        k == Ping || k == RestartNt8 || k == RebootBox || k == KillAll || k == RefreshCredentials;
}

public sealed class GatewayCommand
{
    [JsonPropertyName("command_id")]  public string  CommandId   { get; set; } = "";
    [JsonPropertyName("issued_utc")]  public string  IssuedUtc   { get; set; } = "";
    [JsonPropertyName("kind")]        public string  Kind        { get; set; } = "";
    [JsonPropertyName("args")]        public Dictionary<string, object>? Args { get; set; }
    [JsonPropertyName("expires_utc")] public string  ExpiresUtc  { get; set; } = "";
}

public sealed class CommandsResponse
{
    [JsonPropertyName("commands")]       public List<GatewayCommand> Commands      { get; set; } = new();
    [JsonPropertyName("next_since_utc")] public string               NextSinceUtc  { get; set; } = "";
}

public sealed class CommandAckRequest
{
    [JsonPropertyName("command_id")]    public string  CommandId    { get; set; } = "";
    [JsonPropertyName("result")]        public string  Result       { get; set; } = "";   // success | failed | skipped | expired
    [JsonPropertyName("detail")]        public string? Detail       { get; set; }
    [JsonPropertyName("executed_utc")]  public string  ExecutedUtc  { get; set; } = "";
}

public sealed class CommandAckResponse
{
    [JsonPropertyName("received_utc")] public string ReceivedUtc { get; set; } = "";
}

// Stored in Cosmos container `cycles`, partition key /tenantId.
// Tracks command lifecycle: issued → delivered (when Gateway picks
// it up) → acked. Lets Hive see at a glance which commands are
// pending, which were executed, which expired.
public sealed class CommandDoc
{
    [JsonPropertyName("id")]            public string  Id           { get; set; } = "";  // = command_id
    [JsonPropertyName("tenantId")]      public string  TenantId     { get; set; } = "";
    [JsonPropertyName("machineId")]     public string  MachineId    { get; set; } = "";  // target Cube
    [JsonPropertyName("kind")]          public string  Kind         { get; set; } = "";
    [JsonPropertyName("args")]          public Dictionary<string, object>? Args { get; set; }
    [JsonPropertyName("issuedUtc")]     public string  IssuedUtc    { get; set; } = "";
    [JsonPropertyName("expiresUtc")]    public string  ExpiresUtc   { get; set; } = "";
    [JsonPropertyName("deliveredUtc")]  public string? DeliveredUtc { get; set; }
    [JsonPropertyName("ackedUtc")]      public string? AckedUtc     { get; set; }
    [JsonPropertyName("result")]        public string? Result       { get; set; }
    [JsonPropertyName("detail")]        public string? Detail       { get; set; }
    [JsonPropertyName("docType")]       public string  DocType      { get; set; } = "command";
    [JsonPropertyName("ttl")]           public int     Ttl          { get; set; } = 604800;  // 7d
}

// ============================================================
// AUDIT INGEST (item 11) — request/response for POST /v1/audit/ingest
// ============================================================

// Gateway audit shipper posts one CSV at a time: the filename (used
// for classification + EAV instrument/mid parsing) and the raw file
// text. Tenant + MID are taken from the validated JWT, not the body.
public sealed class AuditIngestRequest
{
    [JsonPropertyName("file_name")] public string  FileName { get; set; } = "";
    [JsonPropertyName("content")]   public string? Content  { get; set; }
}

public sealed class AuditIngestResponse
{
    [JsonPropertyName("status")]        public string Status       { get; set; } = "";  // OK | DUPLICATE
    [JsonPropertyName("log_type")]      public string LogType      { get; set; } = "";
    [JsonPropertyName("rows_parsed")]   public int    RowsParsed   { get; set; }
    [JsonPropertyName("rows_inserted")] public int    RowsInserted { get; set; }
}
