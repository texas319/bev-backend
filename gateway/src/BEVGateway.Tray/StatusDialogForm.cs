// ============================================================
// FILE        : StatusDialogForm.cs
// STATUS      : Phase 1c-2 — NEXUS visual retrofit
// LAST UPD    : 2026-05-28 01:00 CST
// PURPOSE     : NEXUS-themed status window. Renders the live
//               StatusSnapshot as crumbs + status dots instead of
//               a plain MessageBox. Read-only; closes on Done.
// OWNS        : Status display UI.
// CALLED BY   : TrayContext "View Status".
// ============================================================

using System.Drawing.Drawing2D;
using BEVGateway.Shared;
using BEVGateway.Shared.Ipc;

namespace BEVGateway.Tray;

public sealed class StatusDialogForm : NexusForm
{
    private readonly StatusSnapshot? _s;

    public StatusDialogForm(StatusSnapshot? snap)
        : base($"{GatewayConstants.ProductName} \u2014 Status")
    {
        _s = snap;
        Width  = 520;
        Height = 430;

        var body = new Panel
        {
            Location = new Point(1, TitlebarHeight + 1),
            Size     = new Size(Width - 2, Height - TitlebarHeight - 2),
            BackColor = NexusTheme.WindowBg
        };
        body.Paint += PaintBody;
        Controls.Add(body);

        // Position the button relative to the PANEL's own size (it is a
        // child of `body`), not the window. Using window-height here pushed
        // the button below the panel's bottom edge. Anchor bottom-right so
        // it always sits inside the panel regardless of size.
        var done = new NexusButton("DONE")
        {
            Width    = 88,
            Location = new Point(body.Width - 110, body.Height - 48),
            Anchor   = AnchorStyles.Bottom | AnchorStyles.Right
        };
        done.Click += (_, _) => Close();
        body.Controls.Add(done);
    }

    private void PaintBody(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (_s is null)
        {
            DrawHeading(g, "SERVICE UNREACHABLE", NexusTheme.StatusRed, 20, 18);
            using var f = NexusTheme.Mono(9f);
            using var b = new SolidBrush(NexusTheme.TextDim);
            g.DrawString("The Nexus Gateway service is not responding on the\nlocal pipe. It may be starting, stopped, or not installed.",
                f, b, 20, 54);
            return;
        }

        var health = _s.Health;
        var (hc, htext) = health switch
        {
            ConnectionHealth.Green  => (NexusTheme.StatusGreen, "ONLINE"),
            ConnectionHealth.Yellow => (NexusTheme.AttentionAmb, "DEGRADED"),
            ConnectionHealth.Red    => (NexusTheme.StatusRed, "OFFLINE"),
            _                       => (NexusTheme.TextDim, "INITIALIZING")
        };

        DrawHeading(g, htext, hc, 20, 18);

        int y = 56;
        const int step = 26;
        DrawCrumb(g, "BUILD",       Val(_s.Build), ref y, step);
        DrawCrumb(g, "NODE CLASS",  Val(_s.NodeClass), ref y, step);
        DrawCrumb(g, "TIER",        Val(_s.Tier), ref y, step);
        DrawCrumb(g, "TENANT",      Val(_s.TenantId), ref y, step);
        DrawCrumb(g, "MID",         Val(_s.MachineId), ref y, step);
        DrawCrumb(g, "FLEET ROLE",  Val(_s.FleetRole), ref y, step);
        y += 6;
        DrawDotCrumb(g, "SERVER",   _s.ServerUp, ref y, step);
        DrawDotCrumb(g, "HIVE",     _s.HiveUp, ref y, step);
        DrawCrumb(g, "JWT LEFT",    Val($"{_s.TokenMinutesLeft}m"), ref y, step);
        DrawCrumb(g, "LAST HUD",    Val(_s.LastHudUtc), ref y, step);
        y += 6;
        DrawCrumb(g, "LAST SHIP",   _s.LastShipUtc.Length == 0 ? "—"
            : $"ok {_s.LastShipOk}  dup {_s.LastShipDup}  fail {_s.LastShipFailed}", ref y, step);
        DrawCrumb(g, "SHIP TOTAL",  Val(_s.ShipTotalOk.ToString()), ref y, step);
        DrawCrumb(g, "LAST LIVE",   _s.LastLiveUtc.Length == 0 ? "—"
            : $"pushed {_s.LastLivePushed}", ref y, step);
        y += 6;
        DrawCrumb(g, "TRADES ASSIM", _s.TradesAssimilated.ToString("N0"), ref y, step);

        if (!string.IsNullOrEmpty(_s.Error))
        {
            using var f = NexusTheme.Mono(8f);
            using var b = new SolidBrush(NexusTheme.StatusRed);
            g.DrawString($"! {_s.Error}", f, b, 20, y + 4);
        }
    }

    private static string Blank(string s) => string.IsNullOrEmpty(s) ? "\u2014" : s;
    private static string Val(string s) => string.IsNullOrEmpty(s) ? "\u2014" : s.ToUpperInvariant();

    private static void DrawHeading(Graphics g, string text, Color color, int x, int y)
    {
        using var f = NexusTheme.Mono(12f, FontStyle.Bold);
        using var b = new SolidBrush(color);
        g.DrawString(text, f, b, x, y);
    }

    private static void DrawCrumb(Graphics g, string label, string value, ref int y, int step)
    {
        using (var lf = NexusTheme.Mono(8.5f, FontStyle.Bold))
        using (var lb = new SolidBrush(NexusTheme.AccentMid))
            g.DrawString(label, lf, lb, 20, y);
        using (var vf = NexusTheme.Mono(9.5f))
        using (var vb = new SolidBrush(NexusTheme.TextBright))
            g.DrawString(value, vf, vb, 150, y - 1);
        y += step;
    }

    private static void DrawDotCrumb(Graphics g, string label, bool up, ref int y, int step)
    {
        using (var lf = NexusTheme.Mono(8.5f, FontStyle.Bold))
        using (var lb = new SolidBrush(NexusTheme.AccentMid))
            g.DrawString(label, lf, lb, 20, y);

        var dotColor = up ? NexusTheme.StatusGreen : NexusTheme.TextDim;
        if (up)
            using (var glow = new SolidBrush(Color.FromArgb(70, dotColor)))
                g.FillEllipse(glow, 150, y, 12, 12);
        using (var dot = new SolidBrush(dotColor))
            g.FillEllipse(dot, 152, y + 2, 7, 7);

        using (var vf = NexusTheme.Mono(9.5f))
        using (var vb = new SolidBrush(up ? NexusTheme.StatusGreen : NexusTheme.TextDim))
            g.DrawString(up ? "UP" : "DOWN", vf, vb, 168, y - 1);
        y += step;
    }
}
