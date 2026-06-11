// ============================================================
// FILE        : NexusForm.cs
// STATUS      : Phase 1c-2 — NEXUS visual retrofit
// LAST UPD    : 2026-05-28 01:00 CST
// PURPOSE     : Reusable borderless window chrome matching the
//               NEXUS terminal: OS titlebar stripped, 1px
//               BorderMid edge, 28px dark titlebar with a 3px
//               amber accent dash + mono title + 38px X close
//               button. Draggable by the titlebar. Forms inherit
//               this and add their content below TitlebarHeight.
// OWNS        : Window chrome treatment (memo Section 4 + 3.2).
// CALLED BY   : SetupWizardForm, StatusDialogForm.
// ============================================================

using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using BEVGateway.Shared;

namespace BEVGateway.Tray;

public class NexusForm : Form
{
    protected const int TitlebarHeight = 28;
    private const int AccentDashW = 3;
    private const int AccentDashH = 14;
    private const int CloseBtnW = 38;

    private Rectangle _closeRect;
    private bool _closeHover;
    private string _titleText;

    public NexusForm(string title)
    {
        _titleText = title;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition   = FormStartPosition.CenterScreen;
        BackColor       = NexusTheme.WindowBg;
        ForeColor       = NexusTheme.TextNormal;
        Font            = NexusTheme.Mono(9.75f);
        DoubleBuffered  = true;
        Padding         = new Padding(1, TitlebarHeight, 1, 1); // 1px edge + titlebar
        KeyPreview      = true;
        try { Icon = IconFactory.BrandIcon(); } catch { /* non-fatal */ }
        ShowInTaskbar = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // 1px border edge around whole window
        using (var edge = new Pen(NexusTheme.BorderMid, 1))
            g.DrawRectangle(edge, 0, 0, Width - 1, Height - 1);

        // Titlebar fill (PanelBg) + bottom border
        using (var tb = new SolidBrush(NexusTheme.PanelBg))
            g.FillRectangle(tb, 1, 1, Width - 2, TitlebarHeight - 1);
        using (var sep = new Pen(NexusTheme.BorderMid, 1))
            g.DrawLine(sep, 1, TitlebarHeight, Width - 2, TitlebarHeight);

        // Accent dash (3px x 14px amber)
        int dashX = 14;
        int dashY = (TitlebarHeight - AccentDashH) / 2 + 1;
        using (var dash = new SolidBrush(NexusTheme.AccentMid))
            g.FillRectangle(dash, dashX, dashY, AccentDashW, AccentDashH);

        // Title text (Mono 12px SemiBold, TextBright)
        using (var titleFont = NexusTheme.Mono(9f, FontStyle.Bold))
        using (var titleBrush = new SolidBrush(NexusTheme.TextBright))
        {
            var ty = (TitlebarHeight - titleFont.Height) / 2f + 1;
            g.DrawString(_titleText, titleFont, titleBrush, dashX + AccentDashW + 10, ty);
        }

        // X close button (right)
        _closeRect = new Rectangle(Width - CloseBtnW - 1, 1, CloseBtnW, TitlebarHeight - 1);
        if (_closeHover)
            using (var hb = new SolidBrush(NexusTheme.AccentMid))
                g.FillRectangle(hb, _closeRect);
        using (var xFont = NexusTheme.Mono(10f, FontStyle.Bold))
        using (var xBrush = new SolidBrush(_closeHover ? NexusTheme.WindowBg : NexusTheme.TextDim))
        {
            var sz = g.MeasureString("\u2715", xFont);
            g.DrawString("\u2715", xFont, xBrush,
                _closeRect.X + (_closeRect.Width - sz.Width) / 2f,
                _closeRect.Y + (_closeRect.Height - sz.Height) / 2f);
        }
    }

    // ---- titlebar interaction ----

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            if (_closeRect.Contains(e.Location)) { Close(); return; }
            if (e.Y <= TitlebarHeight)
            {
                // Drag the window by the titlebar.
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var was = _closeHover;
        _closeHover = _closeRect.Contains(e.Location);
        if (was != _closeHover) Invalidate(_closeRect);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_closeHover) { _closeHover = false; Invalidate(_closeRect); }
    }

    protected void SetTitle(string title) { _titleText = title; Invalidate(); }

    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HT_CAPTION = 0x2;

    [DllImport("user32.dll")] private static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
}
