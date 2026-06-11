// ============================================================
// FILE        : ServerClient.cs
// STATUS      : Phase 1c-2 — Gateway + Tray binary
// LAST UPD    : 2026-05-27 15:00 CST
// PURPOSE     : Typed HTTP client wrapping calls to
//               https://server.bevcloud.app — provision and
//               tenant/mids. Surfaces structured outcomes the
//               Worker can react to.
// OWNS        : Gateway → Server transport.
// CALLED BY   : GatewayWorker.
// ============================================================

using System.Net.Http.Json;
using BEVGateway.Shared.Wire;
using Microsoft.Extensions.Logging;

namespace BEVGateway.Service.Net;

public interface IServerClient
{
    Task<ProvisionResponse> ProvisionAsync(
        string email, string licenseKey, string fingerprint, string build,
        CancellationToken ct);

    Task<CubeRegisterResponse> RegisterMidAsync(
        string bearer, string mid, string hostname, string email,
        CancellationToken ct);

    // Auto-update: fetch the latest Gateway build manifest from Server.
    Task<(bool ok, GatewayUpdateManifest? manifest, string? error)> GetUpdateManifestAsync(
        string bearer, string mid, CancellationToken ct);

    // Auto-update: download the MSI bytes to a local path. Uses the
    // manifest's DownloadUrl if present, else Server /v1/gateway/download.
    Task<(bool ok, string? error)> DownloadUpdateAsync(
        string bearer, string mid, GatewayUpdateManifest manifest, string destPath,
        CancellationToken ct);
}

public sealed class ServerClient : IServerClient
{
    private readonly IHttpClientFactory _factory;
    private readonly ILogger<ServerClient> _log;

    public ServerClient(IHttpClientFactory factory, ILogger<ServerClient> log)
    {
        _factory = factory;
        _log = log;
    }

    public async Task<ProvisionResponse> ProvisionAsync(
        string email, string licenseKey, string fingerprint, string build,
        CancellationToken ct)
    {
        var client = _factory.CreateClient("server");
        var req = new ProvisionRequest
        {
            Email        = email,
            LicenseKey   = licenseKey,
            Fingerprint  = fingerprint,
            GatewayBuild = build
        };

        try
        {
            var resp = await client.PostAsJsonAsync("/v1/provision", req, ct);
            var body = await resp.Content.ReadFromJsonAsync<ProvisionResponse>(cancellationToken: ct);
            return body ?? new ProvisionResponse { Ok = false, Error = "EMPTY_RESPONSE" };
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Provision call failed.");
            return new ProvisionResponse { Ok = false, Error = "TRANSPORT_FAILURE" };
        }
    }

    public async Task<CubeRegisterResponse> RegisterMidAsync(
        string bearer, string mid, string hostname, string email,
        CancellationToken ct)
    {
        var client = _factory.CreateClient("server");
        using var msg = new HttpRequestMessage(HttpMethod.Post, "/v1/tenant/mids");
        msg.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
        msg.Content = JsonContent.Create(new CubeRegisterRequest
        {
            Mid           = mid,
            Hostname      = hostname,
            FirstSeenUtc  = DateTime.UtcNow.ToString("o"),
            OperatorEmail = email
        });

        try
        {
            var resp = await client.SendAsync(msg, ct);
            var body = await resp.Content.ReadFromJsonAsync<CubeRegisterResponse>(cancellationToken: ct);
            return body ?? new CubeRegisterResponse { Registered = false, Error = "EMPTY_RESPONSE" };
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "tenant/mids call failed.");
            return new CubeRegisterResponse { Registered = false, Error = "TRANSPORT_FAILURE" };
        }
    }

    public async Task<(bool, GatewayUpdateManifest?, string?)> GetUpdateManifestAsync(
        string bearer, string mid, CancellationToken ct)
    {
        var client = _factory.CreateClient("server");
        using var msg = new HttpRequestMessage(HttpMethod.Get, "/v1/gateway/manifest");
        msg.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
        msg.Headers.TryAddWithoutValidation("X-MID", mid);
        try
        {
            var resp = await client.SendAsync(msg, ct);
            if (resp.StatusCode == global::System.Net.HttpStatusCode.Unauthorized)
                return (false, null, "AUTH_EXPIRED");
            if (resp.StatusCode == global::System.Net.HttpStatusCode.NoContent)
                return (true, null, null);                 // no build published yet
            if (!resp.IsSuccessStatusCode)
                return (false, null, $"HTTP_{(int)resp.StatusCode}");
            var body = await resp.Content.ReadFromJsonAsync<GatewayUpdateManifest>(cancellationToken: ct);
            return (true, body, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GetUpdateManifest failed.");
            return (false, null, "TRANSPORT_FAILURE");
        }
    }

    public async Task<(bool, string?)> DownloadUpdateAsync(
        string bearer, string mid, GatewayUpdateManifest manifest, string destPath,
        CancellationToken ct)
    {
        var client = _factory.CreateClient("server");
        client.Timeout = TimeSpan.FromMinutes(10);          // MSI is ~56MB
        HttpRequestMessage msg;
        if (!string.IsNullOrWhiteSpace(manifest.DownloadUrl))
        {
            // Direct (e.g. blob SAS) URL — no auth header needed.
            msg = new HttpRequestMessage(HttpMethod.Get, manifest.DownloadUrl);
        }
        else
        {
            msg = new HttpRequestMessage(HttpMethod.Get,
                $"/v1/gateway/download?version={Uri.EscapeDataString(manifest.Version)}");
            msg.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
            msg.Headers.TryAddWithoutValidation("X-MID", mid);
        }
        try
        {
            using (msg)
            using (var resp = await client.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!resp.IsSuccessStatusCode)
                    return (false, $"HTTP_{(int)resp.StatusCode}");
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                await using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await resp.Content.CopyToAsync(fs, ct);
            }
            return (true, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "DownloadUpdate failed.");
            return (false, "TRANSPORT_FAILURE");
        }
    }
}
