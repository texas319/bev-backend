// ============================================================
// FILE        : IpcClient.cs (Tray)
// STATUS      : Phase 1c-2 — Gateway + Tray binary
// LAST UPD    : 2026-05-27 15:00 CST
// PURPOSE     : Outbound client to the Service's named pipe.
//               One round-trip per call. Short timeouts —
//               Tray UI shouldn't hang if Service is down.
// OWNS        : Tray → Service transport.
// CALLED BY   : TrayContext on every menu action.
// ============================================================

using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using BEVGateway.Shared;
using BEVGateway.Shared.Ipc;

namespace BEVGateway.Tray;

public static class IpcClient
{
    private const int ConnectTimeoutMs = 2000;
    private const int ConnectRetries   = 4;

    public static async Task<IpcResponse?> SendAsync(IpcRequest req, CancellationToken ct = default)
    {
        // Try the connect a few times before giving up. A single missed
        // connect (e.g. landing in the instant between pipe-instance
        // recycles) must NOT be read as "service down" — that caused the
        // green/red flap. Only a sustained inability to connect returns null.
        for (int attempt = 1; attempt <= ConnectRetries; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".",
                    GatewayConstants.TrayPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await client.ConnectAsync(ConnectTimeoutMs, ct);

                using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true)
                    { AutoFlush = true };
                using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);

                await writer.WriteLineAsync(JsonSerializer.Serialize(req));
                var line = await reader.ReadLineAsync(ct);
                if (string.IsNullOrEmpty(line)) return null;

                return JsonSerializer.Deserialize<IpcResponse>(line);
            }
            catch (OperationCanceledException) { return null; }
            catch
            {
                // transient — short backoff and retry unless this was the last attempt
                if (attempt < ConnectRetries)
                {
                    try { await Task.Delay(300, ct); } catch { return null; }
                    continue;
                }
                return null;
            }
        }
        return null;
    }

    public static async Task<StatusSnapshot?> GetStatusAsync(CancellationToken ct = default)
    {
        var resp = await SendAsync(new IpcRequest { Cmd = IpcCommands.GetStatus }, ct);
        return resp?.Status;
    }
}
