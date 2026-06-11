// ============================================================
// FILE        : TrayContext.cs
// STATUS      : Phase 1c-2 — Gateway + Tray binary
// LAST UPD    : 2026-05-27 15:00 CST
// PURPOSE     : ApplicationContext holding the NotifyIcon,
//               context menu, status-poll timer. Maps Gateway
//               health → tray icon color and tooltip.
// OWNS        : Tray UI surface.
// CALLED BY   : Application.Run.
// ============================================================

using System.Diagnostics;
using BEVGateway.Shared;
using BEVGateway.Shared.Ipc;

namespace BEVGateway.Tray;

public sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _poll;
    private StatusSnapshot? _last;
    private int _consecutiveMisses;
    private ConnectionHealth _appliedHealth = ConnectionHealth.Unknown;  // last icon health (blink guard)
    private const int MissesBeforeUnreachable = 4;

    public TrayContext()
    {
        var menu = BuildMenu();

        _icon = new NotifyIcon
        {
            Icon          = IconFactory.CreateForHealth(ConnectionHealth.Unknown),
            Visible       = true,
            Text          = "Nexus Gateway — starting…",
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => ShowStatusDialog();

        _poll = new System.Windows.Forms.Timer { Interval = 5_000 };
        _poll.Tick += async (_, _) => await PollAsync();
        _poll.Start();

        _ = PollAsync();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        var status = new ToolStripMenuItem("Status: starting…") { Enabled = false };
        menu.Items.Add(status);
        menu.Items.Add(new ToolStripSeparator());

        var viewStatus = new ToolStripMenuItem("View Status…");
        viewStatus.Click += (_, _) => ShowStatusDialog();
        menu.Items.Add(viewStatus);

        var setTag = new ToolStripMenuItem("Set VPS Tag…");
        setTag.Click += (_, _) => SetVpsTag();
        menu.Items.Add(setTag);

        var getPin = new ToolStripMenuItem("Get PIN…");
        getPin.Click += async (_, _) =>
        {
            var r = await IpcClient.SendAsync(new IpcRequest { Cmd = IpcCommands.GetPin });
            if (r?.Ok == true && !string.IsNullOrWhiteSpace(r.Pin))
            {
                var shown = r.Pin!.Length == 4 ? $"{r.Pin.Substring(0, 2)}-{r.Pin.Substring(2, 2)}" : r.Pin;
                System.Windows.Forms.MessageBox.Show(
                    $"Your acknowledgment PIN is:\n\n        {shown}\n\n" +
                    "You'll be asked for this when enabling an automated feature (Dragon/Phoenix).",
                    "Nexus Gateway — PIN",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Information);
            }
            else
            {
                ShowBalloon("Get PIN", r?.Message ?? "No PIN available.", false);
            }
        };
        menu.Items.Add(getPin);

        var openLog = new ToolStripMenuItem("Open Log Folder");
        openLog.Click += (_, _) =>
        {
            // Open from the TRAY process (interactive session), not via the
            // service. The service runs as LocalSystem in session 0, so an
            // Explorer window it launches is invisible to the logged-in user.
            try
            {
                var dir = GatewayConstants.LogDir;
                Directory.CreateDirectory(dir); // ensure it exists
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ShowBalloon("Open Log Folder", $"Could not open: {ex.Message}", false);
            }
        };
        menu.Items.Add(openLog);

        menu.Items.Add(new ToolStripSeparator());

        var reprovision = new ToolStripMenuItem("Reprovision (refresh credentials)");
        reprovision.Click += async (_, _) =>
        {
            var r = await IpcClient.SendAsync(new IpcRequest { Cmd = IpcCommands.Reprovision });
            ShowBalloon("Reprovision", r?.Message ?? "(no response)", r?.Ok == true);
        };
        menu.Items.Add(reprovision);

        var setupAgain = new ToolStripMenuItem("Re-run Setup Wizard…");
        setupAgain.Click += (_, _) =>
        {
            using var setup = new SetupWizardForm();
            setup.ShowDialog();
        };
        menu.Items.Add(setupAgain);

        var restart = new ToolStripMenuItem("Restart Service");
        restart.Click += async (_, _) =>
        {
            var r = await IpcClient.SendAsync(new IpcRequest { Cmd = IpcCommands.Restart });
            ShowBalloon("Restart", r?.Message ?? "(no response)", r?.Ok == true);
        };
        menu.Items.Add(restart);

        menu.Items.Add(new ToolStripSeparator());

        var quit = new ToolStripMenuItem("Quit Tray");
        quit.Click += (_, _) => ExitTray();
        menu.Items.Add(quit);

        menu.Tag = status; // stash for later updates
        return menu;
    }

    private async Task PollAsync()
    {
        var snap = await IpcClient.GetStatusAsync();
        ApplyStatus(snap);
    }

    private void ApplyStatus(StatusSnapshot? snap)
    {
        if (snap is null)
        {
            // Transient miss debounce: a single failed poll (e.g. landing
            // in a pipe-instance recycle, or a busy moment) must NOT flip
            // the UI to "unreachable" — that was the green/red flap. Hold
            // the last known-good state until we've missed several polls in
            // a row, which genuinely indicates the service is gone.
            _consecutiveMisses++;
            if (_consecutiveMisses < MissesBeforeUnreachable && _last is not null)
            {
                // keep showing the last good state; do nothing this tick
                return;
            }

            _last = null;
            if (_appliedHealth != ConnectionHealth.Red)
            {
                _icon.Icon = IconFactory.CreateForHealth(ConnectionHealth.Red);
                _appliedHealth = ConnectionHealth.Red;
            }
            _icon.Text = "Nexus Gateway — service unreachable";
            UpdateStatusMenu("Service unreachable");
            _icon.Tag = ConnectionHealth.Red;
            // No popup on outage — the tray icon color + status window already
            // show online/offline. A balloon here was redundant noise.
            return;
        }

        // Got a good reading — reset the miss counter.
        _consecutiveMisses = 0;
        _last = snap;

        // Only rebuild/reassign the icon when health ACTUALLY changes.
        // Reassigning NotifyIcon.Icon every poll forces a tray redraw, which
        // is the on/off blink. Text + status window still refresh every poll.
        if (_appliedHealth != snap.Health)
        {
            _icon.Icon = IconFactory.CreateForHealth(snap.Health);
            _appliedHealth = snap.Health;
        }
        _icon.Text = Truncate($"Nexus Gateway — {snap.StatusText}", 63);
        UpdateStatusMenu(snap.StatusText);
        _icon.Tag = snap.Health;

        // Notification policy (locked GW.0602.26-J): the tray is SILENT on
        // every healthy state and on every recovery. A trader must never see
        // a popup telling them things are fine. The icon color and the status
        // window still update live every poll — that's the at-a-glance signal —
        // but no Windows toast ever fires for Green, for Yellow, for a token-
        // window threshold cross, for Hive-heartbeat jitter, or for recovering
        // from a blip. The ONLY toast that can fire is a genuine, sustained
        // outage (Red), and that is handled in the miss-debounce branch above
        // after MissesBeforeUnreachable consecutive failures (~20s). We do not
        // toast here at all.
    }

    private void UpdateStatusMenu(string text)
    {
        var menu = _icon.ContextMenuStrip;
        if (menu?.Tag is ToolStripMenuItem status)
            status.Text = $"Status: {Truncate(text, 60)}";
    }

    private void ShowStatusBalloon()
    {
        var s = _last;
        if (s is null) { ShowBalloon("Nexus Gateway", "Service unreachable", false); return; }
        var msg =
            $"Tenant: {s.TenantId} ({s.Tier})\n" +
            $"MID:    {s.MachineId}\n" +
            $"JWT:    {s.TokenMinutesLeft}m left\n" +
            $"Last HUD: {s.LastHudUtc}";
        ShowBalloon("Nexus Gateway", msg, s.Health != ConnectionHealth.Red);
    }

    private void ShowStatusDialog()
    {
        using var dlg = new StatusDialogForm(_last);
        dlg.ShowDialog();
    }

    private void SetVpsTag()
    {
        // Vanity VPS tag (C-XXXXXX // TAG in the terminal). Stored in a
        // local file, not the database. Silent — no toast.
        var current = GatewayConstants.ReadCubeTag();
        using var form = new Form
        {
            Text = "Set VPS Tag",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new System.Drawing.Size(340, 130),
            MaximizeBox = false, MinimizeBox = false, BackColor = System.Drawing.Color.FromArgb(10,11,14)
        };
        var lbl = new Label { Text = "VPS tag (vanity name, e.g. VPS_W1):", Left = 14, Top = 14, Width = 310,
            ForeColor = System.Drawing.Color.FromArgb(181,188,198) };
        var box = new TextBox { Left = 14, Top = 40, Width = 310, Text = current };
        var ok = new Button { Text = "Save", Left = 168, Top = 80, Width = 70, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 248, Top = 80, Width = 70, DialogResult = DialogResult.Cancel };
        form.Controls.AddRange(new Control[] { lbl, box, ok, cancel });
        form.AcceptButton = ok; form.CancelButton = cancel;
        if (form.ShowDialog() == DialogResult.OK)
        {
            GatewayConstants.WriteCubeTag(box.Text.Trim());
            UpdateStatusMenu($"VPS tag set: {box.Text.Trim()}");
        }
    }

    private void ShowBalloon(string title, string text, bool good)
    {
        // SILENT GATEWAY (locked GW.0607.26): never fire a Windows balloon
        // toast — those carry a system sound on some boxes, which is the
        // disconnect/reconnect "ping" we are eliminating. Feedback for
        // user-initiated menu actions is surfaced silently on the tray
        // status line instead. No audio, no popup, ever.
        UpdateStatusMenu($"{title}: {text}");
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max - 1) + "…");

    private void ExitTray()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _poll.Stop();
        ExitThread();
    }
}
