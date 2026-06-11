// ============================================================
// FILE        : BevLiveSnapshot.cs  (Gateway wire model)
// STATUS      : Phase 1e — BEV LiveLink ingestion
// PURPOSE     : Exact mirror of EAGLE's FLEET\HUD.<instance_id>.json
//               (per EAGLE/NEXUS Dev "BEV LiveLink" memo, 2026-06-04).
//               The Gateway tails C:\Users\*\...\BEV\Relay\FLEET\HUD.*.json,
//               deserializes into this, normalizes the MID to C-, and
//               forwards to Hive for FleetView. Read-only display state.
// ============================================================

using System.Text.Json.Serialization;

namespace BEVGateway.Shared.Wire;

public sealed class BevLiveSnapshot
{
    // --- 2a. Identity / routing ---
    [JsonPropertyName("instance_id")]   public string  InstanceId   { get; set; } = "";
    [JsonPropertyName("account")]       public string? Account      { get; set; }
    [JsonPropertyName("instrument")]    public string? Instrument   { get; set; }
    [JsonPropertyName("timestamp")]     public string? Timestamp    { get; set; }  // ISO-8601 "O" — EAGLE write time

    // --- 2b. STRATEGY half ---
    [JsonPropertyName("nt8_state")]     public string? Nt8State     { get; set; }
    [JsonPropertyName("paused")]        public bool    Paused       { get; set; }
    [JsonPropertyName("session_halt")]  public bool    SessionHalt  { get; set; }
    [JsonPropertyName("position")]      public int     Position     { get; set; }
    [JsonPropertyName("max_contracts")] public int     MaxContracts { get; set; }
    [JsonPropertyName("pnl_realized")]  public double  PnlRealized  { get; set; }
    [JsonPropertyName("pnl_unrealized")]public double  PnlUnrealized{ get; set; }
    [JsonPropertyName("arm_status")]    public string? ArmStatus    { get; set; }

    // open-family flags decoded from the bitmask in the file
    [JsonPropertyName("trend_open")]        public bool TrendOpen       { get; set; }
    [JsonPropertyName("structure_open")]    public bool StructureOpen   { get; set; }
    [JsonPropertyName("price_action_open")] public bool PriceActionOpen { get; set; }
    [JsonPropertyName("pullback_open")]     public bool PullbackOpen    { get; set; }

    // --- 2c. HUD half ---
    [JsonPropertyName("major_trend")]    public string? MajorTrend     { get; set; }
    [JsonPropertyName("minor_trend")]    public string? MinorTrend     { get; set; }
    [JsonPropertyName("super_trend")]    public string? SuperTrend     { get; set; }
    [JsonPropertyName("current_price")]  public double  CurrentPrice   { get; set; }
    [JsonPropertyName("high_of_day")]    public double  HighOfDay      { get; set; }
    [JsonPropertyName("low_of_day")]     public double  LowOfDay       { get; set; }
    [JsonPropertyName("open_price")]     public double  OpenPrice      { get; set; }
    [JsonPropertyName("today_range")]    public double  TodayRange     { get; set; }
    [JsonPropertyName("atr_ticks")]      public double  AtrTicks       { get; set; }
    [JsonPropertyName("account_balance")]public double  AccountBalance { get; set; }
    [JsonPropertyName("trace_mode")]     public bool    TraceMode      { get; set; }  // trace vs live
    [JsonPropertyName("mid")]            public string? Mid            { get; set; }  // box MID (normalized to C- by Gateway)
    [JsonPropertyName("cube_tag")]       public string? CubeTag        { get; set; }  // vanity VPS label, Gateway-stamped, never DB-persisted
    [JsonPropertyName("heartbeat_ok")]   public bool    HeartbeatOk    { get; set; }

    // per-family { "<family>": { "pnl": double, "win": double } }
    [JsonPropertyName("family_stats")]
    public Dictionary<string, BevFamilyStat>? FamilyStats { get; set; }
}

public sealed class BevFamilyStat
{
    [JsonPropertyName("pnl")] public double Pnl { get; set; }
    [JsonPropertyName("win")] public double Win { get; set; }
}

// Command written to ...\BEV\Relay\command.json (Gateway -> EAGLE).
// EAGLE polls ~1s, executes once per CommandId, id-targeted (no broadcast).
public sealed class BevLiveCommand
{
    [JsonPropertyName("TargetInstance")] public string TargetInstance { get; set; } = "";
    [JsonPropertyName("Cmd")]            public string Cmd            { get; set; } = "";  // PAUSE|RESUME|FLATTEN|KILL
    [JsonPropertyName("CommandId")]      public string CommandId      { get; set; } = "";  // GUID, execute-once
    [JsonPropertyName("TsUtc")]          public string TsUtc          { get; set; } = "";
    [JsonPropertyName("Origin")]         public string Origin         { get; set; } = "GATEWAY";
}
