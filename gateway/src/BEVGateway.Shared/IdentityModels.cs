// ============================================================
// FILE        : IdentityModels.cs
// STATUS      : Phase 1c-2 — Gateway + Tray binary
// LAST UPD    : 2026-05-27 15:00 CST
// PURPOSE     : Persistence shapes for the Cube identity. The
//               encrypted PrivateIdentity holds the license key
//               and current JWT. The plaintext PublicIdentity is
//               what NEXUS reads (subset, no secrets).
// OWNS        : Identity persistence model.
// CALLED BY   : IdentityStore, Service worker, Tray for status.
// ============================================================

using System.Text.Json.Serialization;

namespace BEVGateway.Shared;

// Stored encrypted (DPAPI machine scope) at IdentityPath.
public sealed class PrivateIdentity
{
    [JsonPropertyName("email")]          public string  Email         { get; set; } = "";
    [JsonPropertyName("license_key")]    public string  LicenseKey    { get; set; } = "";
    [JsonPropertyName("tenant_id")]      public string  TenantId      { get; set; } = "";
    [JsonPropertyName("machine_id")]     public string  MachineId     { get; set; } = "";
    [JsonPropertyName("fingerprint")]    public string  Fingerprint   { get; set; } = "";
    [JsonPropertyName("hostname")]       public string  Hostname      { get; set; } = "";

    [JsonPropertyName("tier")]           public string  Tier          { get; set; } = "";
    [JsonPropertyName("dragon_tier_max")] public int    DragonTierMax { get; set; }
    [JsonPropertyName("node_class")]     public string  NodeClass     { get; set; } = "";
    [JsonPropertyName("fleet_role")]     public string  FleetRole     { get; set; } = "";

    [JsonPropertyName("bearer_token")]   public string  BearerToken   { get; set; } = "";
    [JsonPropertyName("token_exp_utc")]  public string  TokenExpUtc   { get; set; } = "";

    // PIN: local mode-escalation ACKNOWLEDGMENT (not a secret). We
    // persist a salted hash (for verification) AND the plaintext, since
    // the operator may retrieve it any time via the Tray "Get PIN"
    // action. The whole identity blob is DPAPI-encrypted at rest, so
    // the plaintext is protected on disk. Empty until first provision.
    [JsonPropertyName("pin_hash")]       public string  PinHash       { get; set; } = "";
    [JsonPropertyName("pin_salt")]       public string  PinSalt       { get; set; } = "";
    [JsonPropertyName("pin_plain")]      public string  PinPlain      { get; set; } = "";
    [JsonPropertyName("pin_set_utc")]    public string  PinSetUtc     { get; set; } = "";

    [JsonPropertyName("bound_utc")]      public string  BoundUtc      { get; set; } = "";
    [JsonPropertyName("last_provision_utc")] public string LastProvisionUtc { get; set; } = "";
    [JsonPropertyName("schema_version")] public int     SchemaVersion { get; set; } = 1;
}

// Written plaintext to the user's NEXUS drop folder. Contains only
// what NEXUS needs to operate. NO license key, NO bearer token,
// NO fingerprint.
public sealed class PublicIdentity
{
    [JsonPropertyName("tenant_id")]   public string TenantId  { get; set; } = "";
    [JsonPropertyName("machine_id")]  public string MachineId { get; set; } = "";
    [JsonPropertyName("node_class")]  public string NodeClass { get; set; } = "";
    [JsonPropertyName("tier")]        public string Tier      { get; set; } = "";
    [JsonPropertyName("fleet_role")]  public string FleetRole { get; set; } = "";
    [JsonPropertyName("bound_utc")]   public string BoundUtc  { get; set; } = "";
    [JsonPropertyName("cube_tag")]    public string CubeTag   { get; set; } = "";
    [JsonPropertyName("written_utc")] public string WrittenUtc { get; set; } = "";
}

// Written by the Tray setup wizard, consumed by the Service worker.
// Lives at C:\ProgramData\BEVGateway\pending-provision.json — the
// directory has Users:Write permissions thanks to the MSI installer
// so the Tray (running in the operator session) can drop the file
// for the Service (LocalSystem) to pick up.
public sealed class PendingProvision
{
    [JsonPropertyName("email")]       public string Email      { get; set; } = "";
    [JsonPropertyName("license_key")] public string LicenseKey { get; set; } = "";
}
