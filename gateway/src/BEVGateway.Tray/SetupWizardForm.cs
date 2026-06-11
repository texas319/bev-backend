// ============================================================
// FILE        : SetupWizardForm.cs
// STATUS      : Phase 1c-2 — NEXUS visual retrofit
// LAST UPD    : 2026-05-28 01:00 CST
// PURPOSE     : First-run setup, fully themed to the NEXUS visual
//               language. Two phases inside one window:
//                 (1) ENTRY  — collect email + license key
//                 (2) DONE   — after provisioning completes, show
//                              the node class (CUBE) + MID as
//                              NEXUS crumbs. (PIN row reserved for
//                              when the PIN backend memo lands.)
//               Writes pending-provision.json, then polls the
//               plaintext NEXUS drop (cube-identity.json) for the
//               provisioning result so it can display the MID the
//               Service obtained.
// OWNS        : First-run UI + completion panel.
// CALLED BY   : Tray Program.cs, tray menu "Re-run Setup".
// ============================================================

using System.Drawing.Drawing2D;
using System.Text.Json;
using BEVGateway.Shared;

namespace BEVGateway.Tray;

public sealed class SetupWizardForm : NexusForm
{
    private readonly Panel _entryPanel;
    private readonly Panel _donePanel;

    private TextBox _email = null!;
    private TextBox _licenseKey = null!;
    private Label _statusLabel = null!;
    private NexusButton _save = null!;

    private Label _doneNode = null!;
    private Label _doneTier = null!;
    private Label _doneMid = null!;
    private Label _donePin = null!;
    private Label _pinNote = null!;
    private System.Windows.Forms.Timer? _pollTimer;
    private string _priorWrittenUtc = "";
    private DateTime _pollDeadline;

    public SetupWizardForm() : base($"{GatewayConstants.ProductName} \u2014 Setup")
    {
        Width  = 560;
        Height = 380;

        _entryPanel = BuildEntryPanel();
        _donePanel  = BuildDonePanel();
        _donePanel.Visible = false;

        Controls.Add(_entryPanel);
        Controls.Add(_donePanel);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        // GATEWAY badge (NEX-style chip) drawn into the titlebar area,
        // right of the title. Amber fill, black mono bold text.
        var g = e.Graphics;
        using var badgeFont = NexusTheme.Mono(8.25f, FontStyle.Bold);
        var label = GatewayConstants.BadgeText;
        var sz = g.MeasureString(label, badgeFont);
        int padX = 10;
        int bw = (int)sz.Width + padX * 2;
        int bh = 16;
        int bx = Width - 38 - 1 - bw - 10; // left of the X button
        int by = (TitlebarHeight - bh) / 2 + 1;
        using (var bg = new SolidBrush(NexusTheme.AccentMid))
            g.FillRectangle(bg, bx, by, bw, bh);
        using (var fg = new SolidBrush(NexusTheme.WindowBg))
            g.DrawString(label, badgeFont, fg, bx + padX, by + (bh - sz.Height) / 2f + 0.5f);
    }

    // ---------- ENTRY PANEL ----------

    private Panel BuildEntryPanel()
    {
        var p = new Panel
        {
            Location = new Point(1, TitlebarHeight + 1),
            Size     = new Size(Width - 2, Height - TitlebarHeight - 2),
            BackColor = NexusTheme.WindowBg
        };

        var heading = new Label
        {
            Text = "WELCOME TO NEXUS GATEWAY",
            Font = NexusTheme.Mono(12f, FontStyle.Bold),
            ForeColor = NexusTheme.AccentMid,
            AutoSize = true,
            Location = new Point(24, 22)
        };

        var sub = new Label
        {
            Text = "Enter your operator credentials. The Gateway will provision\nand bind this machine to your tenant.",
            Font = NexusTheme.Mono(8.5f),
            ForeColor = NexusTheme.TextDim,
            AutoSize = true,
            Location = new Point(24, 52)
        };

        var emailLabel = MakeFieldLabel("EMAIL", new Point(24, 104));
        _email = MakeInput(new Point(140, 100), 380);

        var keyLabel = MakeFieldLabel("LICENSE KEY", new Point(24, 144));
        _licenseKey = MakeInput(new Point(140, 140), 380);
        _licenseKey.CharacterCasing = CharacterCasing.Upper;
        _licenseKey.Font = NexusTheme.Mono(10f);

        var keyHint = new Label
        {
            Text = "XXXX-XXXX-XXXX-XXXX   (spaces or dashes both accepted)",
            Font = NexusTheme.Mono(7.5f),
            ForeColor = NexusTheme.TextMuted,
            AutoSize = true,
            Location = new Point(140, 168)
        };

        _statusLabel = new Label
        {
            Font = NexusTheme.Mono(8f),
            ForeColor = NexusTheme.TextDim,
            AutoSize = false,
            Location = new Point(24, 210),
            Size = new Size(500, 40)
        };

        _save = new NexusButton("SAVE & PROVISION") { Location = new Point(344, 268), Width = 192 };
        _save.Click += OnSave;

        var cancel = new NexusButton("CANCEL", NexusTheme.TextDim) { Location = new Point(248, 268), Width = 88 };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        p.Controls.AddRange(new Control[]
        { heading, sub, emailLabel, _email, keyLabel, _licenseKey, keyHint, _statusLabel, _save, cancel });
        return p;
    }

    // ---------- DONE PANEL ----------

    private Panel BuildDonePanel()
    {
        var p = new Panel
        {
            Location = new Point(1, TitlebarHeight + 1),
            Size     = new Size(Width - 2, Height - TitlebarHeight - 2),
            BackColor = NexusTheme.WindowBg
        };

        var heading = new Label
        {
            Text = "NODE PROVISIONED",
            Font = NexusTheme.Mono(12f, FontStyle.Bold),
            ForeColor = NexusTheme.StatusGreen,
            AutoSize = true,
            Location = new Point(24, 22)
        };

        var sub = new Label
        {
            Text = "This machine is now bound and reporting to the NEXUS platform.",
            Font = NexusTheme.Mono(8.5f),
            ForeColor = NexusTheme.TextDim,
            AutoSize = true,
            Location = new Point(24, 52)
        };

        // Crumbs: NODE CLASS / TIER / MID / PIN
        var nodeLabel = MakeCrumbLabel("NODE CLASS", new Point(24, 92));
        _doneNode = new Label
        {
            Text = "(resolving\u2026)",
            Font = NexusTheme.Mono(11f, FontStyle.Bold),
            ForeColor = NexusTheme.AccentHot,
            AutoSize = true,
            Location = new Point(180, 90)
        };

        var tierLabel = MakeCrumbLabel("TIER", new Point(24, 122));
        _doneTier = new Label
        {
            Text = "(resolving\u2026)",
            Font = NexusTheme.Mono(11f, FontStyle.Bold),
            ForeColor = NexusTheme.TextBright,
            AutoSize = true,
            Location = new Point(180, 120)
        };

        var midLabel = MakeCrumbLabel("MID", new Point(24, 152));
        _doneMid = new Label
        {
            Text = "(resolving\u2026)",
            Font = NexusTheme.Mono(11f, FontStyle.Bold),
            ForeColor = NexusTheme.TextBright,
            AutoSize = true,
            Location = new Point(180, 150)
        };

        var pinLabel = MakeCrumbLabel("PIN", new Point(24, 182));
        _donePin = new Label
        {
            Text = "(resolving\u2026)",
            Font = NexusTheme.Mono(14f, FontStyle.Bold),
            ForeColor = NexusTheme.AccentHot,
            AutoSize = true,
            Location = new Point(180, 178)
        };

        _pinNote = new Label
        {
            Text = "Write this down. It won't be shown again.\nRegenerate anytime from NEXUS Settings \u2192 Security.",
            Font = NexusTheme.Mono(7.5f),
            ForeColor = NexusTheme.AttentionAmb,
            AutoSize = false,
            Location = new Point(24, 212),
            Size = new Size(510, 32)
        };

        var done = new NexusButton("DONE") { Location = new Point(448, 268), Width = 88 };
        done.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };

        p.Controls.AddRange(new Control[]
        { heading, sub, nodeLabel, _doneNode, tierLabel, _doneTier,
          midLabel, _doneMid, pinLabel, _donePin, _pinNote, done });
        return p;
    }

    // ---------- helpers ----------

    private static Label MakeFieldLabel(string text, Point loc) => new()
    {
        Text = text,
        Font = NexusTheme.Mono(8.5f, FontStyle.Bold),
        ForeColor = NexusTheme.TextDim,
        AutoSize = true,
        Location = loc
    };

    private static Label MakeCrumbLabel(string text, Point loc) => new()
    {
        Text = text,
        Font = NexusTheme.Mono(8.5f, FontStyle.Bold),
        ForeColor = NexusTheme.AccentMid,
        AutoSize = true,
        Location = loc
    };

    private TextBox MakeInput(Point loc, int width)
    {
        var tb = new TextBox
        {
            Location = loc,
            Width = width,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = NexusTheme.PanelBgHi,
            ForeColor = NexusTheme.TextBright,
            Font = NexusTheme.Mono(10f)
        };
        return tb;
    }

    private void ShowStatus(string text, Color color)
    {
        _statusLabel.ForeColor = color;
        _statusLabel.Text = text;
    }

    // ---------- save / provision ----------

    private void OnSave(object? sender, EventArgs e)
    {
        var email = _email.Text.Trim();
        var key = NormalizeKey(_licenseKey.Text);

        if (!email.Contains('@'))
        {
            ShowStatus("Email looks invalid.", NexusTheme.StatusRed);
            return;
        }
        if (key.Length != 19 || !LooksLikeLicenseKey(key))
        {
            ShowStatus("License key must be XXXX-XXXX-XXXX-XXXX (Crockford alphabet).", NexusTheme.StatusRed);
            return;
        }

        try
        {
            Directory.CreateDirectory(GatewayConstants.IdentityDir);

            // Record the current NEXUS drop written_utc (if any) so the
            // poll can detect a FRESH provision vs a pre-existing file.
            _priorWrittenUtc = ReadDropWrittenUtc();

            var pending = new PendingProvision { Email = email.ToLowerInvariant(), LicenseKey = key };
            File.WriteAllText(GatewayConstants.PendingProvisionPath,
                JsonSerializer.Serialize(pending, new JsonSerializerOptions { WriteIndented = true }));

            _save.Enabled = false;
            ShowStatus("Provisioning\u2026 waiting for the Gateway service (up to 45s).", NexusTheme.AttentionAmb);
            StartPolling();
        }
        catch (UnauthorizedAccessException)
        {
            ShowStatus("Could not write to ProgramData. Re-run setup as Administrator.", NexusTheme.StatusRed);
        }
        catch (Exception ex)
        {
            ShowStatus($"Save failed: {ex.Message}", NexusTheme.StatusRed);
        }
    }

    private void StartPolling()
    {
        _pollDeadline = DateTime.UtcNow.AddSeconds(45);
        _pollTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        _pollTimer.Tick += (_, _) =>
        {
            var drop = ReadDrop();
            if (drop is not null &&
                !string.IsNullOrEmpty(drop.MachineId) &&
                drop.WrittenUtc != _priorWrittenUtc)
            {
                _pollTimer!.Stop();
                ShowDone(drop);
                return;
            }
            if (DateTime.UtcNow > _pollDeadline)
            {
                _pollTimer!.Stop();
                _save.Enabled = true;
                ShowStatus("Still provisioning. Saved OK \u2014 watch the tray icon; it turns green when bound.",
                    NexusTheme.AttentionAmb);
            }
        };
        _pollTimer.Start();
    }

    private async void ShowDone(PublicIdentity drop)
    {
        _doneNode.Text = string.IsNullOrEmpty(drop.NodeClass) ? "CUBE" : drop.NodeClass;
        _doneTier.Text = string.IsNullOrEmpty(drop.Tier) ? "(none)" : drop.Tier;
        _doneMid.Text  = drop.MachineId;
        _entryPanel.Visible = false;
        _donePanel.Visible = true;

        // Fetch the one-time PIN plaintext from the Service. It is
        // revealed exactly once; if we miss it (e.g. tray restarted),
        // the Service returns null and we show the regenerate hint.
        try
        {
            var resp = await IpcClient.SendAsync(
                new Shared.Ipc.IpcRequest { Cmd = Shared.Ipc.IpcCommands.GetPinOnce });
            var pin = resp?.Pin;
            if (!string.IsNullOrEmpty(pin) && pin.Length == 8)
            {
                _donePin.Text = $"{pin.Substring(0, 4)}-{pin.Substring(4, 4)}";
                _donePin.ForeColor = NexusTheme.AccentHot;
            }
            else
            {
                _donePin.Text = "\u2014\u2014\u2014\u2014\u2014";
                _donePin.Font = NexusTheme.Mono(11f, FontStyle.Bold);
                _donePin.ForeColor = NexusTheme.TextDim;
                _pinNote.Text =
                    "PIN already shown once and cannot be redisplayed.\n" +
                    "Regenerate anytime from NEXUS Settings \u2192 Security.";
            }
        }
        catch
        {
            _donePin.Text = "(service unreachable)";
            _donePin.Font = NexusTheme.Mono(9f, FontStyle.Bold);
            _donePin.ForeColor = NexusTheme.StatusRed;
        }
    }

    private static string DropPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "NinjaTrader 8", "BEV", "Gateway", GatewayConstants.NexusDropFileName);

    private static string ReadDropWrittenUtc()
    {
        var d = ReadDrop();
        return d?.WrittenUtc ?? "";
    }

    private static PublicIdentity? ReadDrop()
    {
        try
        {
            var path = DropPath();
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<PublicIdentity>(json);
        }
        catch { return null; }
    }

    private static string NormalizeKey(string raw) =>
        string.IsNullOrEmpty(raw) ? "" :
        InsertDashes(new string(raw.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray()));

    private static string InsertDashes(string compact)
    {
        if (compact.Length == 19) return compact;
        if (compact.Length != 16) return compact;
        return $"{compact.Substring(0,4)}-{compact.Substring(4,4)}-{compact.Substring(8,4)}-{compact.Substring(12,4)}";
    }

    private static bool LooksLikeLicenseKey(string s)
    {
        if (s.Length != 19) return false;
        if (s[4] != '-' || s[9] != '-' || s[14] != '-') return false;
        const string alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";
        foreach (var c in s)
        {
            if (c == '-') continue;
            if (!alphabet.Contains(c)) return false;
        }
        return true;
    }
}
