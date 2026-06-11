// ============================================================
// FILE        : IpcModels.cs
// STATUS      : Phase 1c-2 — Gateway + Tray binary
// LAST UPD    : 2026-05-27 15:00 CST
// PURPOSE     : Named pipe IPC contract between Tray and Service.
//               Tray asks for status, requests reprovision /
//               restart / quit. Service responds with structured
//               status snapshot.
// OWNS        : IPC wire contract.
// CALLED BY   : Service IPC server, Tray IPC client.
// ============================================================

using System.Text.Json.Serialization;

namespace BEVGateway.Shared.Ipc;

public static class IpcCommands
{
    public const string GetStatus    = "STATUS";
    public const string Reprovision  = "REPROVISION";
    public const string Restart      = "RESTART";
    public const string Quit         = "QUIT";
    public const string OpenLogDir   = "OPEN_LOG_DIR";
    public const string GetPinOnce   = "GET_PIN_ONCE";
    public const string GetPin       = "GET_PIN";   // repeatable reveal (acknowledgment, not a secret)
}

public sealed class IpcRequest
{
    [JsonPropertyName("cmd")]   public string Cmd   { get; set; } = "";
    [JsonPropertyName("args")]  public Dictionary<string, string>? Args { get; set; }
}

public enum ConnectionHealth
{
    Green,    // everything up
    Yellow,   // partial degradation (one of Server/Hive flaky)
    Red,      // hard down (no JWT, can't reach Server, etc.)
    Unknown   // service still warming up
}

public sealed class StatusSnapshot
{
    [JsonPropertyName("ok")]                public bool    Ok                { get; set; }
    [JsonPropertyName("build")]             public string  Build             { get; set; } = "";

    [JsonPropertyName("provisioned")]       public bool    Provisioned       { get; set; }
    [JsonPropertyName("tenant_id")]         public string  TenantId          { get; set; } = "";
    [JsonPropertyName("machine_id")]        public string  MachineId         { get; set; } = "";
    [JsonPropertyName("tier")]              public string  Tier              { get; set; } = "";
    [JsonPropertyName("node_class")]        public string  NodeClass         { get; set; } = "";
    [JsonPropertyName("fleet_role")]        public string  FleetRole         { get; set; } = "";

    [JsonPropertyName("token_exp_utc")]     public string  TokenExpUtc       { get; set; } = "";
    [JsonPropertyName("token_minutes_left")] public int    TokenMinutesLeft  { get; set; }

    [JsonPropertyName("server_up")]         public bool    ServerUp          { get; set; }
    [JsonPropertyName("hive_up")]           public bool    HiveUp            { get; set; }
    [JsonPropertyName("last_hud_utc")]      public string  LastHudUtc        { get; set; } = "";
    [JsonPropertyName("last_hud_status")]   public string  LastHudStatus     { get; set; } = "";
    [JsonPropertyName("last_command_utc")]  public string  LastCommandUtc    { get; set; } = "";

    // Audit shipper activity — surfaced in the tray status window so the
    // operator can see shipping health without tailing logs.
    [JsonPropertyName("last_ship_utc")]      public string  LastShipUtc       { get; set; } = "";
    [JsonPropertyName("last_ship_ok")]       public int     LastShipOk        { get; set; }
    [JsonPropertyName("last_ship_dup")]      public int     LastShipDup       { get; set; }
    [JsonPropertyName("last_ship_failed")]   public int     LastShipFailed    { get; set; }
    [JsonPropertyName("ship_total_ok")]      public long    ShipTotalOk       { get; set; }

    // LiveLink forwarding activity.
    [JsonPropertyName("last_live_utc")]      public string  LastLiveUtc       { get; set; } = "";
    [JsonPropertyName("last_live_pushed")]   public int     LastLivePushed    { get; set; }

    // Global trades assimilated by the Hive (fleet-wide TCA count) — the
    // same number Phoenix/Dragon reason against. Identical on every box.
    [JsonPropertyName("trades_assimilated")] public long    TradesAssimilated { get; set; }

    [JsonPropertyName("health")]            public ConnectionHealth Health   { get; set; } = ConnectionHealth.Unknown;
    [JsonPropertyName("status_text")]       public string  StatusText        { get; set; } = "";

    [JsonPropertyName("error")]             public string? Error             { get; set; }
}

public sealed class IpcResponse
{
    [JsonPropertyName("ok")]      public bool             Ok      { get; set; }
    [JsonPropertyName("message")] public string?          Message { get; set; }
    [JsonPropertyName("status")]  public StatusSnapshot?  Status  { get; set; }
    // One-time PIN plaintext, only populated in response to GET_PIN_ONCE
    // and only on the very first read after provision. Null thereafter.
    [JsonPropertyName("pin")]     public string?          Pin     { get; set; }
}
