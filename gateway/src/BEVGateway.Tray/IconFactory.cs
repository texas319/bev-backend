// ============================================================
// FILE        : IconFactory.cs
// STATUS      : Phase 1c-2 — Gateway + Tray binary
// LAST UPD    : 2026-05-27 15:00 CST
// PURPOSE     : Draws a tinted circle on a 16×16 (or 32×32) bitmap
//               and returns it as a system tray Icon. Avoids the
//               need to ship .ico assets and lets us redraw on
//               health changes cheaply.
// OWNS        : Tray icon rendering.
// CALLED BY   : TrayContext on status updates.
// ============================================================

using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using BEVGateway.Shared;
using BEVGateway.Shared.Ipc;

namespace BEVGateway.Tray;

public static class IconFactory
{
    public static Icon CreateForHealth(ConnectionHealth health)
    {
        var color = health switch
        {
            ConnectionHealth.Green   => NexusTheme.StatusGreen,    // #52C77C
            ConnectionHealth.Yellow  => NexusTheme.AttentionAmb,   // #F0D454
            ConnectionHealth.Red     => NexusTheme.StatusRed,      // #FF6B6B
            _                        => NexusTheme.TextDim         // #5F6772
        };

        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // NEXUS header style: an amber accent bar on the left, then a
            // status-colored square with a black outline. Status color maps:
            //   Green  = both Server + Hive up
            //   Amber  = half up / connecting
            //   Red    = both offline
            // (Whether this icon sits in the always-visible tray vs the
            // overflow is a Windows per-user setting, not app-controllable.)

            // Amber accent bar (the NEXUS header marker) down the left edge.
            using (var bar = new SolidBrush(NexusTheme.AccentMid))   // #FFA940
                g.FillRectangle(bar, 2, 4, 4, size - 8);

            // Status box: filled square with a black outline, to the right
            // of the accent bar.
            var boxX = 9;
            var boxY = 5;
            var boxW = size - boxX - 4;   // ~19px
            var boxH = size - boxY - 5;   // ~22px

            using (var fill = new SolidBrush(color))
                g.FillRectangle(fill, boxX, boxY, boxW, boxH);

            using (var outline = new Pen(Color.FromArgb(235, 0, 0, 0), 2f))
                g.DrawRectangle(outline, boxX, boxY, boxW, boxH);
        }

        var hIcon = bmp.GetHicon();
        try
        {
            var icon = (Icon)Icon.FromHandle(hIcon).Clone();
            return icon;
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    // Cached branded app icon (amber NEXUS square with "N"). Used for the
    // window chrome and the notification toast so they show the BEV/NEXUS
    // brand instead of a generic Windows glyph + the raw process name.
    private static Icon? _brand;

    public static Icon BrandIcon()
    {
        if (_brand is not null) return _brand;

        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Rounded amber square — the NEXUS terminal brand mark.
            var amber = NexusTheme.AccentMid;     // #FFA940
            using (var path = RoundedRect(2, 2, size - 4, size - 4, 6))
            {
                using var fill = new SolidBrush(amber);
                g.FillPath(fill, path);
                using var edge = new Pen(NexusTheme.AccentHot, 1f);
                g.DrawPath(edge, path);
            }

            // Black "N" centered.
            using var f = NexusTheme.Mono(15f, FontStyle.Bold);
            using var tb = new SolidBrush(NexusTheme.WindowBg);  // near-black
            var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            g.DrawString("N", f, tb, new RectangleF(1, 0, size, size), sf);
        }

        var hIcon = bmp.GetHicon();
        try { _brand = (Icon)Icon.FromHandle(hIcon).Clone(); }
        finally { DestroyIcon(hIcon); }
        return _brand;
    }

    private static GraphicsPath RoundedRect(int x, int y, int w, int h, int r)
    {
        var path = new GraphicsPath();
        path.AddArc(x, y, r, r, 180, 90);
        path.AddArc(x + w - r, y, r, r, 270, 90);
        path.AddArc(x + w - r, y + h - r, r, r, 0, 90);
        path.AddArc(x, y + h - r, r, r, 90, 90);
        path.CloseFigure();
        return path;
    }
}
