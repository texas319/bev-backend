// ============================================================
// FILE        : TrayIpcServer.cs
// STATUS      : Phase 1c-2 — Gateway + Tray binary
// LAST UPD    : 2026-05-27 15:00 CST
// PURPOSE     : Named pipe server the Tray helper connects to.
//               Tray calls STATUS to fetch the live snapshot;
//               REPROVISION clears identity and writes a fresh
//               pending file; OPEN_LOG_DIR launches Explorer
//               into the log folder; RESTART self-restarts via
//               Environment.Exit + Windows service auto-restart;
//               QUIT just disconnects the pipe (placebo).
// OWNS        : Tray ↔ Service IPC surface.
// CALLED BY   : Service host as IHostedService.
// ============================================================

using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using BEVGateway.Service.Storage;
using BEVGateway.Service.System;
using BEVGateway.Shared;
using BEVGateway.Shared.Ipc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BEVGateway.Service.Ipc;

public sealed class TrayIpcServer : BackgroundService
{
    private readonly StatusReporter _status;
    private readonly IIdentityStore _store;
    private readonly ISystemActions _actions;
    private readonly IPinService _pin;
    private readonly ILogger<TrayIpcServer> _log;

    public TrayIpcServer(
        StatusReporter status, IIdentityStore store, ISystemActions actions,
        IPinService pin, ILogger<TrayIpcServer> log)
    {
        _status = status;
        _store = store;
        _actions = actions;
        _pin = pin;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("IPC server listening on pipe '{Pipe}'.", GatewayConstants.TrayPipeName);

        // Run a POOL of listener instances rather than a single one. With
        // one instance, a tray connect could still miss during the instant
        // between handing off a connection and re-arming the next listener.
        // Several concurrent listeners mean a connect always finds a free
        // one — this removes the ONLINE<->UNREACHABLE pipe race at the
        // source rather than papering over it with client retries.
        const int listenerPoolSize = 4;
        var listeners = new List<Task>();
        for (int i = 0; i < listenerPoolSize; i++)
            listeners.Add(ListenLoopAsync(stoppingToken));
        await Task.WhenAll(listeners);
    }

    private async Task ListenLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreateServer();
                await server.WaitForConnectionAsync(stoppingToken);
                // Handle this connection inline (this loop is one of several
                // in the pool, so others remain free to accept). The handler
                // owns disposal of the stream in its own finally block.
                await HandleConnectionAsync(server, stoppingToken);
            }
            catch (OperationCanceledException) { server?.Dispose(); break; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "IPC accept failed; retrying.");
                try { server?.Dispose(); } catch { }
                await Task.Delay(500, stoppingToken);
            }
        }
    }

    private static NamedPipeServerStream CreateServer()
    {
        // Allow Authenticated Users to connect — Tray runs in the
        // operator's session, Service runs as LocalSystem; they
        // need to interop across session boundaries.
        if (OperatingSystem.IsWindows())
        {
            var security = new PipeSecurity();
            var authUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
            security.AddAccessRule(new PipeAccessRule(authUsers,
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                AccessControlType.Allow));

            return NamedPipeServerStreamAcl.Create(
                GatewayConstants.TrayPipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                inBufferSize: 8192,
                outBufferSize: 8192,
                pipeSecurity: security);
        }

        return new NamedPipeServerStream(
            GatewayConstants.TrayPipeName, PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true)
                { AutoFlush = true };

            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrEmpty(line)) return;

            IpcRequest? req;
            try { req = JsonSerializer.Deserialize<IpcRequest>(line); }
            catch { req = null; }

            IpcResponse resp = req?.Cmd switch
            {
                IpcCommands.GetStatus    => new IpcResponse { Ok = true, Status = _status.Get() },
                IpcCommands.GetPinOnce   => new IpcResponse { Ok = true, Pin = _pin.RevealOnce() },
                IpcCommands.GetPin       => await DoGetPinAsync(ct),
                IpcCommands.OpenLogDir   => DoOpenLogDir(),
                IpcCommands.Reprovision  => await DoReprovisionAsync(ct),
                IpcCommands.Restart      => DoRestart(),
                IpcCommands.Quit         => new IpcResponse { Ok = true, Message = "Tray quit acknowledged." },
                _                        => new IpcResponse { Ok = false, Message = $"Unknown cmd: {req?.Cmd}" }
            };

            await writer.WriteLineAsync(JsonSerializer.Serialize(resp));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "IPC handle failed.");
        }
        finally
        {
            try { if (server.IsConnected) server.Disconnect(); } catch { }
            server.Dispose();
        }
    }

    private IpcResponse DoOpenLogDir()
    {
        _actions.OpenLogDirectory();
        return new IpcResponse { Ok = true, Message = "Opening log directory." };
    }

    // Repeatable PIN reveal. The PIN is an acknowledgment, not a secret,
    // so a licensed operator can retrieve it any time. Reads the
    // plaintext persisted in the DPAPI-encrypted identity blob.
    private async Task<IpcResponse> DoGetPinAsync(CancellationToken ct)
    {
        var id = await _store.LoadAsync(ct);
        if (id is null || string.IsNullOrWhiteSpace(id.PinPlain))
        {
            // Fall back to a freshly-staged one-time value if present
            // (e.g. just provisioned, not yet persisted on reload).
            var once = _pin.RevealOnce();
            return once is not null
                ? new IpcResponse { Ok = true, Pin = once }
                : new IpcResponse { Ok = false, Message = "No PIN set. Re-provision to generate one." };
        }
        return new IpcResponse { Ok = true, Pin = id.PinPlain };
    }

    private async Task<IpcResponse> DoReprovisionAsync(CancellationToken ct)
    {
        // Reprovision means: forget the JWT (force refresh) but keep
        // license + fingerprint. Useful when the JWT is in a bad state
        // but the license is still valid. For "I changed license" you
        // re-run the setup wizard which writes a new pending file.
        var ident = await _store.LoadAsync(ct);
        if (ident is null)
        {
            return new IpcResponse { Ok = false, Message = "No identity to reprovision." };
        }
        ident.BearerToken = "";
        ident.TokenExpUtc = "";
        await _store.SaveAsync(ident, ct);
        return new IpcResponse { Ok = true, Message = "JWT cleared. Worker will reprovision on next tick." };
    }

    private static IpcResponse DoRestart()
    {
        // Schedule self-exit; Windows service recovery brings us back.
        Task.Run(async () =>
        {
            await Task.Delay(500);
            Environment.Exit(1); // non-zero so SCM treats as crash and auto-restarts
        });
        return new IpcResponse { Ok = true, Message = "Service restart scheduled." };
    }
}
