// ============================================================
// FILE        : BEV Hive Fleet Aggregate.cs
// STATUS      : Phase 1f — cross-cube fleet aggregation (Build 1)
// PURPOSE     : The single cross-cube source of truth for the FleetView.
//               A NEXUS surface on a cube only sees its own cube; only the
//               Hive receives every cube's frames, so the Hive is the only
//               place the fleet can be assembled. This builds the
//               fleet.aggregate payload pushed on the rail, consumed
//               identically by the web terminal and the NT8 fleet view.
//
//               Rollup:  per-instance snapshot -> per-account (sum P&L /
//               position across that account's instances, balance once) ->
//               per-cube subtotal -> fleet total. LIVE vs SIM split on
//               trace_mode (false=live, true=sim); the headline fleet total
//               counts LIVE accounts only, SIM is a separate split line.
//
// SCOPE NOTE  : fleet_live has no tenant_id column; the rollup spans all
//               live rows, matching GetFleetRosterAsync (also un-scoped) and
//               the current single-tenant reality, then is pushed to the
//               ingesting tenant's group. Multi-tenant requires a mid->tenant
//               column (flagged in the build memo), not a code change here.
// ============================================================

using System.Globalization;
using System.Text.Json;

namespace BEV.Hive.Services;

// ---- DTOs: serialize directly to the fleet.aggregate payload ----

public sealed class FleetMoney
{
    public decimal realized   { get; set; }
    public decimal unrealized { get; set; }
    public decimal net        { get; set; }
    public decimal balance    { get; set; }
}

public sealed class FleetAccount
{
    public string  account    { get; set; } = "";
    public decimal realized   { get; set; }
    public decimal unrealized { get; set; }
    public decimal net        { get; set; }
    public int     contracts  { get; set; }
    public decimal balance    { get; set; }
    public string  mode       { get; set; } = "live"; // live | sim
}

public sealed class FleetCube
{
    public string             mid      { get; set; } = "";
    public string             cube_tag { get; set; } = "";
    public FleetMoney         subtotal { get; set; } = new();
    public List<FleetAccount> accounts { get; set; } = new();
}

public sealed class FleetAggregate
{
    public FleetMoney      fleet_total { get; set; } = new(); // LIVE only
    public FleetMoney      sim_split   { get; set; } = new(); // SIM only
    public List<FleetCube> cubes       { get; set; } = new();
}

// ---- pure rollup: no DB, fully unit-testable ----

public static class FleetAggregator
{
    // rows: (mid, snapshotJson) for every live instance in audit.fleet_live.
    public static FleetAggregate Build(IEnumerable<(string mid, string snapshotJson)> rows)
    {
        // mid -> (account -> running account agg)
        var byCube = new Dictionary<string, Dictionary<string, FleetAccount>>(StringComparer.OrdinalIgnoreCase);
        var cubeTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (mid, snap) in rows)
        {
            if (string.IsNullOrWhiteSpace(mid) || string.IsNullOrWhiteSpace(snap)) continue;
            JsonElement root;
            try { using var doc = JsonDocument.Parse(snap); root = doc.RootElement.Clone(); }
            catch { continue; }

            var account = Str(root, "account");
            if (account.Length == 0) continue;

            var tag = Str(root, "cube_tag");
            if (tag.Length > 0) cubeTags[mid] = tag;

            var realized = Dec(root, "pnl_realized");
            var unreal   = Dec(root, "pnl_unrealized");
            var balance  = Dec(root, "account_balance");
            var position = (int)Dec(root, "position");
            var sim      = Bool(root, "trace_mode"); // true => sim

            if (!byCube.TryGetValue(mid, out var accs))
            {
                accs = new Dictionary<string, FleetAccount>(StringComparer.OrdinalIgnoreCase);
                byCube[mid] = accs;
            }
            if (!accs.TryGetValue(account, out var a))
            {
                a = new FleetAccount { account = account, mode = sim ? "sim" : "live", balance = balance };
                accs[account] = a;
            }
            // sum P&L and position across the account's instances;
            // balance is per-account (not per-instrument) -> take once.
            a.realized   += realized;
            a.unrealized += unreal;
            a.contracts  += position;
            if (a.balance == 0m && balance != 0m) a.balance = balance;
            // any sim instance marks the account sim (defensive; an account
            // should not mix modes across its instruments).
            if (sim) a.mode = "sim";
        }

        var agg = new FleetAggregate();

        foreach (var mid in byCube.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            var cube = new FleetCube { mid = mid, cube_tag = cubeTags.TryGetValue(mid, out var t) ? t : "" };
            foreach (var a in byCube[mid].Values.OrderBy(x => x.account, StringComparer.OrdinalIgnoreCase))
            {
                a.net = a.realized + a.unrealized;
                cube.accounts.Add(a);

                // per-cube subtotal counts live accounts only (matches the
                // fleet headline rule); sim rolls into the fleet sim_split.
                if (a.mode == "sim")
                {
                    agg.sim_split.realized   += a.realized;
                    agg.sim_split.unrealized += a.unrealized;
                }
                else
                {
                    cube.subtotal.realized   += a.realized;
                    cube.subtotal.unrealized += a.unrealized;
                    cube.subtotal.balance    += a.balance;
                }
            }
            cube.subtotal.net = cube.subtotal.realized + cube.subtotal.unrealized;

            agg.fleet_total.realized   += cube.subtotal.realized;
            agg.fleet_total.unrealized += cube.subtotal.unrealized;
            agg.fleet_total.balance    += cube.subtotal.balance;

            agg.cubes.Add(cube);
        }

        agg.fleet_total.net = agg.fleet_total.realized + agg.fleet_total.unrealized;
        agg.sim_split.net   = agg.sim_split.realized + agg.sim_split.unrealized;
        return agg;
    }

    // ---- defensive snapshot field readers (keys may be number or string) ----

    private static string Str(JsonElement o, string key)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var e)
           ? (e.ValueKind == JsonValueKind.String ? (e.GetString() ?? "") : e.ToString())
           : "";

    private static decimal Dec(JsonElement o, string key)
    {
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(key, out var e)) return 0m;
        if (e.ValueKind == JsonValueKind.Number && e.TryGetDecimal(out var d)) return d;
        if (e.ValueKind == JsonValueKind.String &&
            decimal.TryParse(e.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var ds)) return ds;
        return 0m;
    }

    private static bool Bool(JsonElement o, string key)
    {
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(key, out var e)) return false;
        if (e.ValueKind == JsonValueKind.True)  return true;
        if (e.ValueKind == JsonValueKind.False) return false;
        if (e.ValueKind == JsonValueKind.String) return string.Equals(e.GetString(), "true", StringComparison.OrdinalIgnoreCase);
        return false;
    }
}
