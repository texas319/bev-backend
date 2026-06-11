// ============================================================
// FILE        : BEV Hive Csv.cs
// PURPOSE     : Minimal RFC-4180 CSV parser. Handles quoted fields,
//               embedded commas, escaped double-quotes (""), and CRLF.
//               Used by AuditRouter. Returns rows of string cells;
//               row 0 is the header.
// ============================================================

namespace BEV.Hive.Services;

public static class Csv
{
    public static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>();
        if (string.IsNullOrEmpty(text)) return rows;

        // strip UTF-8 BOM if present
        if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);

        var row = new List<string>();
        var cell = new System.Text.StringBuilder();
        bool inQuotes = false;
        int i = 0, n = text.Length;

        while (i < n)
        {
            char ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < n && text[i + 1] == '"') { cell.Append('"'); i += 2; continue; }
                    inQuotes = false; i++; continue;
                }
                cell.Append(ch); i++; continue;
            }
            switch (ch)
            {
                case '"':
                    inQuotes = true; i++; break;
                case ',':
                    row.Add(cell.ToString()); cell.Clear(); i++; break;
                case '\r':
                    i++; break;            // swallow; \n ends the line
                case '\n':
                    row.Add(cell.ToString()); cell.Clear();
                    rows.Add(row); row = new List<string>(); i++; break;
                default:
                    cell.Append(ch); i++; break;
            }
        }
        // trailing cell/row (file without final newline)
        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }
        return rows;
    }
}
