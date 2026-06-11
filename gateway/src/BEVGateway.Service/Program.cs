// ============================================================
// FILE        : Program.cs
// STATUS      : Phase 1c-2 — Gateway + Tray binary
// LAST UPD    : 2026-05-27 15:00 CST
// PURPOSE     : Windows service host bootstrap. Wires HTTP
//               clients, identity store, fingerprint service,
//               worker. Runs as Windows service in production;
//               supports --console for interactive testing.
// OWNS        : Service lifecycle.
// CALLED BY   : Windows Service Control Manager (production)
//               OR direct CLI launch with --console (dev).
// ============================================================

using BEVGateway.Service.Ipc;
using BEVGateway.Service.Net;
using BEVGateway.Service.Storage;
using BEVGateway.Service.System;
using BEVGateway.Service.Worker;
using BEVGateway.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;

namespace BEVGateway.Service;

public static class Program
{
    public static async Task Main(string[] args)
    {
        // Ensure log dir exists before any logger writes to it.
        try { Directory.CreateDirectory(GatewayConstants.LogDir); } catch { /* best effort */ }

        var builder = Host.CreateApplicationBuilder(args);

        builder.Services.AddWindowsService(opts =>
        {
            opts.ServiceName = GatewayConstants.ServiceName;
        });

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        if (OperatingSystem.IsWindows())
        {
            builder.Logging.AddEventLog(new EventLogSettings
            {
                SourceName = GatewayConstants.ServiceName,
                LogName    = "Application"
            });
        }
        builder.Logging.AddProvider(new FileLoggerProvider(
            Path.Combine(GatewayConstants.LogDir, $"gateway-{DateTime.UtcNow:MMddyyyy}.log")));
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        builder.Services.AddHttpClient("server", c =>
        {
            c.BaseAddress = new Uri(GatewayConstants.ServerBaseUrl);
            c.Timeout = TimeSpan.FromSeconds(30);
        });
        builder.Services.AddHttpClient("hive", c =>
        {
            c.BaseAddress = new Uri(GatewayConstants.HiveBaseUrl);
            c.Timeout = TimeSpan.FromSeconds(60); // long-poll-friendly
        });

        builder.Services.AddSingleton<IFingerprintService, WmiFingerprintService>();
        builder.Services.AddSingleton<INodeClassDetector, NodeClassDetector>();
        builder.Services.AddSingleton<IPinService, PinService>();
        builder.Services.AddSingleton<IIdentityStore, IdentityStore>();
        builder.Services.AddSingleton<IServerClient, ServerClient>();
        builder.Services.AddSingleton<IHiveClient, HiveClient>();
        builder.Services.AddSingleton<ISystemActions, SystemActions>();
        builder.Services.AddSingleton<StatusReporter>();

        builder.Services.AddHostedService<GatewayWorker>();
        builder.Services.AddHostedService<TrayIpcServer>();

        // Never let one background service's unhandled exception tear
        // down the entire host (default is StopHost). If the worker
        // throws, log it and keep the service alive - the watchdog and
        // command loop must survive transient faults.
        builder.Services.Configure<HostOptions>(o =>
            o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

        await builder.Build().RunAsync();
    }
}
