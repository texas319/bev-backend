// ============================================================
// FILE        : BEV Hive Audit Router.cs
// STATUS      : Phase 2 — audit pipeline (item 11)
// PURPOSE     : Classify an EAGLE audit CSV and parse it into typed
//               rows for the matching audit.* table. This is the C#
//               port of the reference router validated against the
//               live bundle BEV-EAGLE-06-02-26 (163 CSV, 0 failures).
// NOTE        : Column type maps + quirk handling are intentionally
//               identical to the validated Python reference. Any
//               schema change must update BOTH.
// ============================================================

using System.Globalization;
using System.Text.Json;

namespace BEV.Hive.Services;

public sealed class AuditParseResult
{
    public List<Dictionary<string, object?>> Rows { get; } = new();
}

public static class AuditRouter
{
    // ---- classification by filename ----
    public static string? Classify(string filename)
    {
        var f = filename.ToUpperInvariant();
        if (f.Contains("-TCA-"))              return "tca";
        if (f.Contains("-ORDER-LIFECYCLE-"))  return "order_lifecycle";
        if (f.Contains("-SIGEVAL-"))          return "sigeval";
        if (f.Contains("-BARSNAP-"))          return "barsnap";
        if (f.Contains("-DIAG-"))             return "diag";
        if (f.Contains("-PERF-"))             return "perf";
        if (f.Contains("-SETTINGS-"))         return "settings";
        if (f.Contains("-SPEC-"))             return "spec";
        if (f.Contains("-LIVE-"))             return "trace";  // bare LIVE = TRACE
        return null;
    }

    // ---- type maps (mirror of validated reference) ----
    private static readonly HashSet<string> TcaInt = new() {
        "qty","sig_concurrent_long","sig_concurrent_short","sig_breadth_member_count",
        "bars_held","max_qty_setting","other_open_positions_at_signal","session_trade_index",
        "position_entry_count_at_fill","position_total_qty_at_fill","leg_index_within_position",
        "pre_adjustment_size","final_size","bars_since_last_reversal" };
    private static readonly HashSet<string> TcaTs = new() { "broker_fill_timestamp_utc" };

    private static readonly HashSet<string> SigInt = new() {
        "breadth_tier_int","l_count","s_count","n_count","open_positions","bar_index" };
    private static readonly HashSet<string> SigJson = new() { "gate_details" };

    private static readonly HashSet<string> OlInt = new() {
        "quantity_total","quantity_filled","quantity_remaining" };
    private static readonly HashSet<string> OlJson = new() { "notes" };

    private static readonly Dictionary<string,string> TraceMap = new() {
        {"Timestamp","timestamp_utc"},{"EventType","event_type"},{"Family","family"},
        {"Direction","direction"},{"TradeId","trade_id"},{"Qty","qty"},{"Price","price"},
        {"StopPrice","stop_price"},{"StopTicks","stop_ticks"},{"T1Price","t1_price"},
        {"T2Price","t2_price"},{"T3Price","t3_price"},{"PnLDollar","pnl_dollar"},
        {"StopSource","stop_source"},{"BarIndex","bar_index"},{"Notes","notes"},
        {"AcctBalanceBefore","acct_balance_before"},{"AcctBalanceAfter","acct_balance_after"},
        {"MachineId","mid"},{"BreadthReq","breadth_req"},{"BreadthLive","breadth_live"},
        {"InternalIp","internal_ip"},{"ExternalIp","external_ip"},{"MacAddress","mac_address"},
        {"codename","codename"},{"major_version","major_version"},{"build_version","build_version"} };
    private static readonly HashSet<string> TraceInt = new() { "qty","stop_ticks","bar_index" };

    // Numeric columns: anything not in Int/Ts/Json and not obviously text is
    // parsed as numeric only when it looks numeric (TryNum returns null otherwise),
    // so wide tables coerce safely without an explicit Num set per the reference.

    public static AuditParseResult BuildRows(string logType, string content, string filename)
    {
        var res = new AuditParseResult();
        var records = Csv.Parse(content);
        if (records.Count == 0) return res;

        // Rotated/concatenated files can START with a data row, with the
        // real header appearing on a later line, and can repeat the header
        // mid-file. Find the FIRST row that looks like the header for this
        // log type, and use it. (perf/settings/spec are vertical EAV/kv and
        // do not need this — they ignore the header anyway.)
        int headerIdx = FindHeaderRow(logType, records);
        var header = headerIdx >= 0 ? records[headerIdx] : records[0];

        switch (logType)
        {
            case "tca":
            case "sigeval":
            case "barsnap":
            case "order_lifecycle":
                BuildWide(logType, header, records, res);
                break;
            case "trace":
            case "diag":
                BuildTrace(header, records, res, filename);
                break;
            case "spec":
                BuildSpec(records, res, filename);
                break;
            case "perf":
                BuildEav(records, res, filename, group:false);
                break;
            case "settings":
                BuildEav(records, res, filename, group:true);
                break;
        }
        return res;
    }

    // A header row contains the type's signature column name as literal text.
    private static int FindHeaderRow(string logType, List<List<string>> records)
    {
        for (int i = 0; i < records.Count; i++)
            if (IsHeaderRow(logType, records[i])) return i;
        return -1;
    }

    private static bool IsHeaderRow(string logType, List<string> rec)
    {
        if (rec.Count == 0) return false;
        var first = rec[0].Trim().Trim('"').TrimStart('\uFEFF');
        return logType switch
        {
            "tca"             => first == "timestamp_utc",
            "sigeval"         => first == "timestamp_utc",
            "barsnap"         => first == "timestamp_utc",
            "order_lifecycle" => first == "timestamp_utc",
            "trace"           => first == "Timestamp",
            "diag"            => first == "Timestamp",
            _                 => false,
        };
    }

    private static void BuildWide(string logType, List<string> header,
        List<List<string>> records, AuditParseResult res)
    {
        var ints  = logType switch { "tca" => TcaInt, "sigeval" => SigInt, "order_lifecycle" => OlInt, _ => new HashSet<string>() };
        var jsons = logType switch { "sigeval" => SigJson, "order_lifecycle" => OlJson, _ => new HashSet<string>() };
        var tss   = logType == "tca" ? TcaTs : new HashSet<string>();
        // barsnap ints handled by name prefix below

        for (int i = 0; i < records.Count; i++)
        {
            var rec = records[i];
            if (rec.Count == 0 || rec.All(string.IsNullOrWhiteSpace)) continue;
            // Skip the header itself and any repeated header mid-file.
            if (IsHeaderRow(logType, rec)) continue;
            var o = new Dictionary<string, object?>();
            for (int c = 0; c < header.Count && c < rec.Count; c++)
            {
                var key = header[c]; var val = rec[c];
                if (key == "timestamp_utc") { o[key] = ParseIso(val); continue; }
                if (jsons.Contains(key))    { o[key] = ParseJson(val); continue; }
                if (tss.Contains(key))      { o[key] = ParseIso(val); continue; }
                if (ints.Contains(key) || (logType=="barsnap" && IsBarsnapInt(key)))
                                            { o[key] = TryInt(val); continue; }
                // numeric-if-looks-numeric, else text
                o[key] = SmartCell(val);
            }
            o["session_date"] = SessionDate(o.TryGetValue("timestamp_utc", out var t) ? t as DateTime? : null);
            res.Rows.Add(o);
        }
    }

    private static bool IsBarsnapInt(string k) =>
        k is "breadth_tier_int" or "breadth_long_count" or "breadth_short_count"
          or "breadth_active_count" or "open_positions_qty"
          || k.StartsWith("bars_since_signal_p", StringComparison.Ordinal);

    private static void BuildTrace(List<string> header, List<List<string>> records,
        AuditParseResult res, string filename)
    {
        for (int i = 0; i < records.Count; i++)
        {
            var rec = records[i];
            if (rec.Count == 0) continue;
            // skip blank separator rows and the header (incl. repeats mid-file)
            var firstCell = rec[0].Trim().Trim('"').TrimStart('\uFEFF');
            if (firstCell.Length == 0 || firstCell == "Timestamp") continue;

            var o = new Dictionary<string, object?>();
            var raw = new Dictionary<string, string?>();
            for (int c = 0; c < header.Count && c < rec.Count; c++)
            {
                var src = header[c]; var val = rec[c];
                raw[src] = val;
                if (!TraceMap.TryGetValue(src, out var tgt)) continue;
                if (tgt == "timestamp_utc")      o[tgt] = ParseTraceTs(val);
                else if (TraceInt.Contains(tgt)) o[tgt] = TryInt(val);
                else if (tgt is "price" or "stop_price" or "t1_price" or "t2_price"
                              or "t3_price" or "pnl_dollar" or "acct_balance_before"
                              or "acct_balance_after")
                                                  o[tgt] = TryNum(val);
                else                              o[tgt] = TextCell(val);
            }
            o["raw_payload"]  = raw;
            o["session_date"] = SessionDate(o.TryGetValue("timestamp_utc", out var t) ? t as DateTime? : null);
            res.Rows.Add(o);
        }
    }

    private static void BuildSpec(List<List<string>> records, AuditParseResult res, string filename)
    {
        var kv = new Dictionary<string, string?>();
        for (int i = 1; i < records.Count; i++)
        {
            var rec = records[i];
            if (rec.Count < 2) continue;
            var k = rec[0].Trim();
            if (k.Length > 0) kv[k] = rec[1].Trim();
        }
        DateTime? ts = kv.TryGetValue("timestamp_utc", out var v) ? ParseIso(v) : null;
        res.Rows.Add(new Dictionary<string, object?> {
            ["timestamp_utc"]   = ts,
            ["session_date"]    = SessionDate(ts),
            ["mid"]             = MidFromName(filename),
            ["machine_id_full"] = kv.GetValueOrDefault("machine_id_full"),
            ["raw_payload"]     = kv,
        });
    }

    private static void BuildEav(List<List<string>> records, AuditParseResult res,
        string filename, bool group)
    {
        var mid = MidFromName(filename); var inst = InstrumentFromName(filename);
        var sd  = DateFromName(filename);
        for (int i = 1; i < records.Count; i++)
        {
            var rec = records[i];
            if (rec.Count < 3) continue;
            var o = new Dictionary<string, object?> {
                ["session_date"] = sd, ["mid"] = mid, ["instrument"] = inst,
            };
            if (group) { o["key"] = rec[0]; o["value"] = rec[1]; o["grp"] = rec[2]; }       // settings: key,value,group
            else       { o["section"] = rec[0]; o["key"] = rec[1]; o["value"] = rec[2]; }   // perf: section,key,value
            res.Rows.Add(o);
        }
    }

    // ---- cell coercion helpers ----
    private static object? SmartCell(string v)
    {
        var n = TryNum(v);
        return n ?? (object?)TextCell(v);
    }
    private static string? TextCell(string v)
    {
        if (v is null) return null;
        var s = v.Trim().Trim('"');
        return s.Length == 0 ? null : s;
    }
    private static double? TryNum(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var s = v.Trim().Trim('"');
        if (s.Length == 0) return null;
        return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;
    }
    private static long? TryInt(string v)
    {
        var n = TryNum(v);
        return n is null ? null : (long?)(long)Math.Round(n.Value);
    }
    private static DateTime? ParseIso(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        // RoundtripKind cannot be combined with AdjustToUniversal (they are
        // mutually exclusive). RoundtripKind already honors the offset/Z in
        // the string; normalize to UTC afterward.
        if (DateTime.TryParse(v.Trim().Trim('"'), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var dt))
            return dt.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
                : dt.ToUniversalTime();
        return null;
    }
    private static DateTime? ParseTraceTs(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var s = v.Trim().Trim('"');
        if (DateTime.TryParseExact(s, "MM-dd-yy HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
            return dt;
        return ParseIso(s);
    }
    private static object? ParseJson(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        try { return JsonDocument.Parse(v.Trim()).RootElement.Clone(); }
        catch (JsonException) { return null; }
    }
    // US EASTERN is the platform's canonical time zone for ALL session
    // accounting. We convert the UTC timestamp to America/New_York, which
    // handles EST (-5) vs EDT (-4) automatically across DST — a fixed
    // -4 offset was wrong for Nov-Mar. Windows uses "Eastern Standard
    // Time"; Linux/cross-plat uses the IANA id "America/New_York". We try
    // the IANA id first (works on .NET 6+ on Windows too via ICU) and
    // fall back to the Windows id.
    private static readonly TimeZoneInfo EasternTz = ResolveEastern();
    private static TimeZoneInfo ResolveEastern()
    {
        foreach (var id in new[] { "America/New_York", "Eastern Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        // Last-resort fixed EST if neither id resolves (should not happen).
        return TimeZoneInfo.CreateCustomTimeZone("BEV-Eastern-EST",
            TimeSpan.FromHours(-5), "BEV Eastern (EST)", "BEV Eastern (EST)");
    }

    // Trading session date = the America/New_York CALENDAR DATE of the
    // event's UTC timestamp. DST-correct year-round.
    private static DateTime? SessionDate(DateTime? dt)
    {
        if (dt is null) return null;
        var utc = dt.Value.Kind == DateTimeKind.Utc
            ? dt.Value
            : DateTime.SpecifyKind(dt.Value, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(utc, EasternTz).Date;
    }

    // ---- filename parsers ----
    private static string? MidFromName(string f)
    {
        var m = System.Text.RegularExpressions.Regex.Match(f, "MID-([0-9A-Fa-f]{6})");
        return m.Success ? NormalizeMid(m.Groups[1].Value) : null;
    }
    // Public accessor for the ingest handler's ledger-MID fallback.
    public static string? MidFromNamePublic(string f) => MidFromName(f);

    // Canonical MID format across the ENTIRE platform: "C-XXXXXX" (Cube
    // prefix + 6 hex, upper). Anything bare (e.g. "55A857") or already
    // prefixed is normalized to exactly one "C-" prefix. Used for both
    // the ledger MID (from the JWT) and the data-table mid (from the
    // file name) so the database is uniform everywhere.
    public static string? NormalizeMid(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim().ToUpperInvariant();
        // strip any existing C- (or repeated) prefix, then re-add one
        while (s.StartsWith("C-")) s = s.Substring(2);
        if (s.Length == 0) return null;          // blank -> NULL, never a bare "C-"
        return "C-" + s;
    }
    private static string? InstrumentFromName(string f)
    {
        var m = System.Text.RegularExpressions.Regex.Match(f.ToUpperInvariant(), "-([A-Z]{1,4})-\\d{6}-");
        return m.Success ? m.Groups[1].Value : null;
    }
    private static DateTime? DateFromName(string f)
    {
        var m = System.Text.RegularExpressions.Regex.Match(f, "-(\\d{6})-\\d{4,6}-");
        if (m.Success && DateTime.TryParseExact(m.Groups[1].Value, "MMddyy",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        return null;
    }
}
