// ============================================================
// FILE        : HiveClient.cs
// STATUS      : Phase 1c-2 — Gateway + Tray binary
// LAST UPD    : 2026-05-27 15:00 CST
// PURPOSE     : Typed HTTP client for https://hive.bevcloud.app
//               — health, hud-snapshot, commands long-poll,
//               command-ack. Carries the Bearer JWT + X-MID
//               header on authenticated requests.
// OWNS        : Gateway → Hive transport.
// CALLED BY   : GatewayWorker.
// ============================================================

using System.Net.Http.Json;
using System.Text.Json;
using BEVGateway.Shared.Wire;
using Microsoft.Extensions.Logging;

namespace BEVGateway.Service.Net;

public interface IHiveClient
{
    Task<bool> HealthAsync(CancellationToken ct);

    Task<(bool ok, HudSnapshotResponse? body, string? error)> PushHudAsync(
        string bearer, string mid, HudSnapshotRequest payload, CancellationToken ct);

    Task<(bool ok, string? error)> PushLiveAsync(
        string bearer, string mid, BevLiveSnapshot snap, CancellationToken ct);

    Task<(bool ok, long count)> GetAssimilatedAsync(
        string bearer, string mid, CancellationToken ct);

    Task<(bool ok, CommandsResponse? body, string? error)> PollCommandsAsync(
        string bearer, string mid, DateTime sinceUtc, CancellationToken ct);

    Task<(bool ok, string? error)> AckCommandAsync(
        string bearer, string mid, CommandAckRequest ack, CancellationToken ct);

    // Audit shipper: POST one CSV to /v1/audit/ingest. Returns the
    // parsed response so the caller can distinguish OK vs DUPLICATE and
    // tally rows. error is non-null only on transport/auth failure.
    Task<(bool ok, AuditIngestResponse? body, string? error)> ShipAuditAsync(
        string bearer, string mid, AuditIngestRequest payload, CancellationToken ct);
}

public sealed class HiveClient : IHiveClient
{
    private readonly IHttpClientFactory _factory;
    private readonly ILogger<HiveClient> _log;

    public HiveClient(IHttpClientFactory factory, ILogger<HiveClient> log)
    {
        _factory = factory;
        _log = log;
    }

    public async Task<bool> HealthAsync(CancellationToken ct)
    {
        try
        {
            var client = _factory.CreateClient("hive");
            using var resp = await client.GetAsync("/v1/health", ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<(bool, HudSnapshotResponse?, string?)> PushHudAsync(
        string bearer, string mid, HudSnapshotRequest payload, CancellationToken ct)
    {
        var client = _factory.CreateClient("hive");
        using var msg = new HttpRequestMessage(HttpMethod.Post, "/v1/hud-snapshot");
        msg.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
        msg.Headers.TryAddWithoutValidation("X-MID", mid);
        msg.Content = JsonContent.Create(payload);

        try
        {
            var resp = await client.SendAsync(msg, ct);
            if (resp.StatusCode == global::System.Net.HttpStatusCode.Unauthorized)
                return (false, null, "AUTH_EXPIRED");
            if (!resp.IsSuccessStatusCode)
                return (false, null, $"HTTP_{(int)resp.StatusCode}");
            var body = await resp.Content.ReadFromJsonAsync<HudSnapshotResponse>(cancellationToken: ct);
            return (true, body, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PushHud failed.");
            return (false, null, "TRANSPORT_FAILURE");
        }
    }

    public async Task<(bool, string?)> PushLiveAsync(
        string bearer, string mid, BevLiveSnapshot snap, CancellationToken ct)
    {
        var client = _factory.CreateClient("hive");
        using var msg = new HttpRequestMessage(HttpMethod.Post, "/v1/fleet/live");
        msg.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
        msg.Headers.TryAddWithoutValidation("X-MID", mid);
        msg.Content = JsonContent.Create(snap);
        try
        {
            var resp = await client.SendAsync(msg, ct);
            if (resp.StatusCode == global::System.Net.HttpStatusCode.Unauthorized)
                return (false, "AUTH_EXPIRED");
            if (!resp.IsSuccessStatusCode)
                return (false, $"HTTP_{(int)resp.StatusCode}");
            return (true, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PushLive failed.");
            return (false, "TRANSPORT_FAILURE");
        }
    }

    public async Task<(bool ok, long count)> GetAssimilatedAsync(
        string bearer, string mid, CancellationToken ct)
    {
        var client = _factory.CreateClient("hive");
        using var msg = new HttpRequestMessage(HttpMethod.Get, "/v1/assimilated");
        msg.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
        msg.Headers.TryAddWithoutValidation("X-MID", mid);
        try
        {
            var resp = await client.SendAsync(msg, ct);
            if (!resp.IsSuccessStatusCode) return (false, 0);
            using var doc = await JsonDocument.ParseAsync(
                await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (doc.RootElement.TryGetProperty("trades_assimilated", out var el))
                return (true, el.GetInt64());
            return (false, 0);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GetAssimilated failed.");
            return (false, 0);
        }
    }

    public async Task<(bool, CommandsResponse?, string?)> PollCommandsAsync(
        string bearer, string mid, DateTime sinceUtc, CancellationToken ct)
    {
        var client = _factory.CreateClient("hive");
        var path = $"/v1/commands?since_utc={Uri.EscapeDataString(sinceUtc.ToString("o"))}";
        using var msg = new HttpRequestMessage(HttpMethod.Get, path);
        msg.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
        msg.Headers.TryAddWithoutValidation("X-MID", mid);

        try
        {
            var resp = await client.SendAsync(msg, ct);
            if (resp.StatusCode == global::System.Net.HttpStatusCode.Unauthorized)
                return (false, null, "AUTH_EXPIRED");
            if (!resp.IsSuccessStatusCode)
                return (false, null, $"HTTP_{(int)resp.StatusCode}");
            var body = await resp.Content.ReadFromJsonAsync<CommandsResponse>(cancellationToken: ct);
            return (true, body, null);
        }
        catch (TaskCanceledException)
        {
            // Long-poll timeout — not an error.
            return (true, new CommandsResponse(), null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PollCommands failed.");
            return (false, null, "TRANSPORT_FAILURE");
        }
    }

    public async Task<(bool, string?)> AckCommandAsync(
        string bearer, string mid, CommandAckRequest ack, CancellationToken ct)
    {
        var client = _factory.CreateClient("hive");
        using var msg = new HttpRequestMessage(HttpMethod.Post, "/v1/command-ack");
        msg.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
        msg.Headers.TryAddWithoutValidation("X-MID", mid);
        msg.Content = JsonContent.Create(ack);

        try
        {
            var resp = await client.SendAsync(msg, ct);
            if (!resp.IsSuccessStatusCode)
                return (false, $"HTTP_{(int)resp.StatusCode}");
            return (true, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AckCommand failed.");
            return (false, "TRANSPORT_FAILURE");
        }
    }

    public async Task<(bool, AuditIngestResponse?, string?)> ShipAuditAsync(
        string bearer, string mid, AuditIngestRequest payload, CancellationToken ct)
    {
        var client = _factory.CreateClient("hive");
        using var msg = new HttpRequestMessage(HttpMethod.Post, "/v1/audit/ingest");
        msg.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
        msg.Headers.TryAddWithoutValidation("X-MID", mid);
        msg.Content = JsonContent.Create(payload);

        try
        {
            var resp = await client.SendAsync(msg, ct);
            if (resp.StatusCode == global::System.Net.HttpStatusCode.Unauthorized)
                return (false, null, "AUTH_EXPIRED");
            // 500 (parse failure on a single bad file) is reported as an
            // error so the caller can log + skip WITHOUT marking the file
            // shipped — it will be retried on a later build/scan.
            if (!resp.IsSuccessStatusCode)
                return (false, null, $"HTTP_{(int)resp.StatusCode}");
            var body = await resp.Content.ReadFromJsonAsync<AuditIngestResponse>(cancellationToken: ct);
            return (true, body, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ShipAudit failed for {File}.", payload.FileName);
            return (false, null, "TRANSPORT_FAILURE");
        }
    }
}
