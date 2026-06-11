// ============================================================
// FILE        : BEV Hive Function Catalog.cs
// STATUS      : Phase 2 / Drop 2 — control plane (the WRITE plane)
// PURPOSE     : Loads BEV Function Catalog.json (the NEXUS-owned contract)
//               and exposes lookups for the /v1/invoke endpoint. The
//               catalog is the server-side ALLOW-LIST: a function_id not
//               in here cannot be invoked, period.
//
//   Tiers : read | write | nuclear
//   Scoping rule (from catalog): any arg carrying an account/fleet_id is
//     checked against the caller's JWT fleet_ids; out-of-scope = 403.
//   Nuclear: needs args.confirm == true AND a confirm_token round-trip.
//
// The catalog file is copied next to the build and read at startup.
// ============================================================

using System.Text.Json;
using System.Text.Json.Serialization;

namespace BEV.Hive.Services;

public sealed class CatalogFunction
{
    [JsonPropertyName("function_id")] public string FunctionId { get; set; } = "";
    [JsonPropertyName("label")]       public string Label      { get; set; } = "";
    [JsonPropertyName("category")]    public string Category   { get; set; } = "";
    [JsonPropertyName("tier")]        public string Tier       { get; set; } = "";   // read|write|nuclear
    [JsonPropertyName("args")]        public JsonElement Args  { get; set; }
    [JsonPropertyName("returns")]     public JsonElement Returns { get; set; }
    [JsonPropertyName("underlying")]  public string Underlying { get; set; } = "";
}

public sealed class FunctionCatalogRoot
{
    [JsonPropertyName("catalog_version")] public string Version { get; set; } = "";
    [JsonPropertyName("functions")]       public List<CatalogFunction> Functions { get; set; } = new();
}

public interface IFunctionCatalog
{
    bool TryGet(string functionId, out CatalogFunction fn);
    string Version { get; }
    int Count { get; }
}

public sealed class FunctionCatalog : IFunctionCatalog
{
    private readonly Dictionary<string, CatalogFunction> _byId;
    public string Version { get; }
    public int Count => _byId.Count;

    public FunctionCatalog()
    {
        // catalog ships beside the assembly; fall back to app root.
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "function-catalog.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "function-catalog.json"),
            Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "", "site", "wwwroot", "function-catalog.json"),
        };
        FunctionCatalogRoot? root = null;
        foreach (var p in candidates)
        {
            try { if (File.Exists(p)) { root = JsonSerializer.Deserialize<FunctionCatalogRoot>(File.ReadAllText(p)); break; } }
            catch { /* try next */ }
        }
        root ??= new FunctionCatalogRoot();
        Version = root.Version;
        _byId = root.Functions.ToDictionary(f => f.FunctionId, f => f, StringComparer.OrdinalIgnoreCase);
    }

    public bool TryGet(string functionId, out CatalogFunction fn)
        => _byId.TryGetValue(functionId ?? "", out fn!);
}
