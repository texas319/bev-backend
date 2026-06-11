// ============================================================
// FILE        : NexusTheme.cs
// STATUS      : Phase 1c-2 — NEXUS visual retrofit
// LAST UPD    : 2026-05-28 01:00 CST
// PURPOSE     : The canonical BEV/NEXUS visual language tokens,
//               transcribed from the NEXUS Visual Language Spec
//               (PART 21 BEVxNEXUS_Theme is authoritative). Exact
//               hex values — do not approximate. Both the Gateway
//               installer chrome and the Tray WinForms surfaces
//               consume these.
// OWNS        : Design tokens for all Gateway UI surfaces.
// CALLED BY   : Tray forms, icon factory, status dialog.
// ============================================================

using System.Drawing;
using System.Drawing.Text;

namespace BEVGateway.Shared;

public static class NexusTheme
{
    // ---- BACKGROUNDS ----
    public static readonly Color WindowBg    = FromHex("#000000");
    public static readonly Color HeaderBg    = FromHex("#050507");
    public static readonly Color PanelBg     = FromHex("#0A0B0E");
    public static readonly Color PanelBgHi   = FromHex("#11141A");

    // ---- BORDERS + DIVIDERS ----
    public static readonly Color BorderDim   = FromHex("#161A22");
    public static readonly Color BorderMid   = FromHex("#1F2530");
    public static readonly Color BorderHot   = FromHex("#2B3340");
    public static readonly Color DividerDim  = FromHex("#12161C");

    // ---- AMBER FAMILY (brand signature) ----
    public static readonly Color AccentDim   = FromHex("#8C5B22");
    public static readonly Color AccentMid   = FromHex("#FFA940"); // THE brand amber
    public static readonly Color AccentHot   = FromHex("#FFBE6A");

    // ---- TEXT ----
    public static readonly Color TextBright  = FromHex("#E8EBF0");
    public static readonly Color TextNormal  = FromHex("#B5BCC6");
    public static readonly Color TextDim     = FromHex("#5F6772");
    public static readonly Color TextMuted   = FromHex("#3B414B");

    // ---- STATUS / SEMANTIC ----
    public static readonly Color StatusGreen = FromHex("#52C77C");
    public static readonly Color StatusRed   = FromHex("#FF6B6B");
    public static readonly Color StatusRedDim = FromHex("#8B3939");
    public static readonly Color AttentionAmb = FromHex("#F0D454");
    public static readonly Color VoiceCyan   = FromHex("#4DB6E8");

    // ---- LICENSE CHIP (brand chip) ----
    public static readonly Color LicenseChipBg     = FromHex("#211507");
    public static readonly Color LicenseChipBorder = FromHex("#FFA940");
    public static readonly Color LicenseChipText   = FromHex("#FFC880");

    // ---- TYPOGRAPHY ----
    // Mono is the workhorse. Resolve IBM Plex Mono if present, else
    // fall back through the memo-sanctioned chain to a guaranteed
    // monospace face.
    private static readonly string MonoFamily = ResolveFamily(
        new[] { "IBM Plex Mono", "Cascadia Mono", "Consolas", "Courier New" });
    private static readonly string SansFamily = ResolveFamily(
        new[] { "IBM Plex Sans", "Segoe UI", "Tahoma" });

    public static Font Mono(float size, FontStyle style = FontStyle.Regular)
        => new(MonoFamily, size, style, GraphicsUnit.Point);

    public static Font Sans(float size, FontStyle style = FontStyle.Regular)
        => new(SansFamily, size, style, GraphicsUnit.Point);

    private static string ResolveFamily(string[] candidates)
    {
        try
        {
            using var installed = new InstalledFontCollection();
            var names = new HashSet<string>(
                installed.Families.Select(f => f.Name),
                StringComparer.OrdinalIgnoreCase);
            foreach (var c in candidates)
                if (names.Contains(c)) return c;
        }
        catch { /* fall through */ }
        return candidates[^1];
    }

    public static Color FromHex(string hex)
    {
        hex = hex.TrimStart('#');
        return Color.FromArgb(
            255,
            Convert.ToInt32(hex.Substring(0, 2), 16),
            Convert.ToInt32(hex.Substring(2, 2), 16),
            Convert.ToInt32(hex.Substring(4, 2), 16));
    }
}
