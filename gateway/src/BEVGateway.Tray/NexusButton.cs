// ============================================================
// FILE        : NexusButton.cs
// STATUS      : Phase 1c-2 — NEXUS visual retrofit
// LAST UPD    : 2026-05-28 01:00 CST
// PURPOSE     : Flat bordered button matching the NEXUS accent
//               (3.7) and destructive (3.6) patterns: transparent
//               fill, 1px colored border, mono text; on hover the
//               fill inverts to the accent color with black text.
// OWNS        : Button styling.
// CALLED BY   : Tray forms.
// ============================================================

using System.Drawing.Drawing2D;
using BEVGateway.Shared;

namespace BEVGateway.Tray;

public sealed class NexusButton : Button
{
    private Color _accent = NexusTheme.AccentMid;
    private bool _hover;

    public NexusButton(string text, Color? accent = null)
    {
        Text          = text;
        if (accent.HasValue) _accent = accent.Value;
        FlatStyle     = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor     = NexusTheme.PanelBg;
        ForeColor     = _accent;
        Font          = NexusTheme.Mono(8.5f, FontStyle.Bold);
        Cursor        = Cursors.Hand;
        Height        = 30;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.AllPaintingInWmPaint, true);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);

        var fill = _hover ? _accent : NexusTheme.PanelBg;
        var textColor = _hover ? NexusTheme.WindowBg : _accent;

        using (var fb = new SolidBrush(fill)) g.FillRectangle(fb, rect);
        using (var border = new Pen(_accent, 1)) g.DrawRectangle(border, rect);

        TextRenderer.DrawText(g, Text, Font, rect, textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}
