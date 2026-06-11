// ============================================================
// FILE        : Program.cs (Tray)
// STATUS      : Phase 1c-2 — Gateway + Tray binary
// LAST UPD    : 2026-05-27 15:00 CST
// PURPOSE     : Tray helper entry point. Single-instance mutex,
//               then either the setup wizard (first run) or the
//               tray icon (subsequent runs).
// OWNS        : Tray helper lifecycle.
// CALLED BY   : Windows Run-at-logon registration installed by
//               the MSI.
// ============================================================

using System.Runtime.InteropServices;
using BEVGateway.Shared;

namespace BEVGateway.Tray;

internal static class Program
{
    [DllImport("shell32.dll", SetLastError = true)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string AppID);

    [STAThread]
    private static void Main(string[] args)
    {
        // Give the process a friendly Application User Model ID. This is what
        // Windows shows as the app name on toast notifications — without it,
        // toasts are labelled with the raw process name "BEVGateway.Tray".
        // With it, they read "Nexus Gateway".
        try { SetCurrentProcessExplicitAppUserModelID("BirdsEyeView.NexusGateway"); }
        catch { /* non-fatal: older shells just fall back to process name */ }

        using var mutex = new Mutex(true, @"Global\BEVGateway.Tray.SingleInstance", out var firstInstance);
        if (!firstInstance) return;

        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        // If no identity exists AND no pending-provision is queued,
        // show the setup wizard first. After the wizard writes the
        // pending file, the Gateway service will provision and the
        // tray reflects "ready" state shortly thereafter.
        var identityExists  = File.Exists(GatewayConstants.IdentityPath);
        var pendingExists   = File.Exists(GatewayConstants.PendingProvisionPath);

        if (!identityExists && !pendingExists)
        {
            using var setup = new SetupWizardForm();
            var dr = setup.ShowDialog();
            if (dr != DialogResult.OK)
            {
                // User cancelled. Still show the tray so they can re-trigger
                // setup later via the menu.
            }
        }

        Application.Run(new TrayContext());
    }
}
