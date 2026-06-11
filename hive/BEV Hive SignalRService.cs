// ============================================================
// FILE        : BEV Hive SignalRService.cs
// STATUS      : Phase 2 / Drop 1 — live rails transport
// PURPOSE     : Talk to Azure SignalR Service (Serverless) via its REST
//               API. No binding extension — full control over the client
//               token (userId), group membership, and scoped pushes.
//
//   Parses AzureSignalRConnectionString (Endpoint=...;AccessKey=...).
//   - BuildClientNegotiate(hub, userId)  -> { url, accessToken }
//       client token: HS256 JWT, aud = client url, nameid = userId.
//   - AddUserToGroupAsync(hub, userId, group)
//   - SendToGroupAsync(hub, group, target, args)
//   - SendToUserAsync(hub, userId, target, args)
//   - BroadcastAsync(hub, target, args)
//       management tokens: HS256 JWT, aud = the REST api url.
//
//   Frames are sent with target = "frame" and a single arg = the
//   envelope object { panel_id, payload, ts_utc } (or row-delta form).
// ============================================================

using System.IdentityModel.Tokens.Jwt;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Logging;

namespace BEV.Hive.Services;

public interface ISignalRService
{
    (string url, string accessToken) BuildClientNegotiate(string hub, string userId);
    Task AddUserToGroupAsync(string hub, string userId, string group, CancellationToken ct);
    Task SendToGroupAsync(string hub, string group, object envelope, CancellationToken ct);
    Task SendToUserAsync(string hub, string userId, object envelope, CancellationToken ct);
    Task BroadcastAsync(string hub, object envelope, CancellationToken ct);
    bool Configured { get; }
}

public sealed class SignalRService : ISignalRService
{
    private readonly string _endpoint;     // https://bev-signalr.service.signalr.net
    private readonly byte[] _key;          // AccessKey bytes
    private readonly bool _configured;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<SignalRService> _log;
    private const string Target = "frame";

    public bool Configured => _configured;

    public SignalRService(IHttpClientFactory http, ILogger<SignalRService> log)
    {
        _http = http; _log = log;
        var conn = Environment.GetEnvironmentVariable("AzureSignalRConnectionString") ?? "";
        string ep = "", key = "";
        foreach (var part in conn.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = part.IndexOf('=');
            if (i < 0) continue;
            var k = part.Substring(0, i).Trim();
            var v = part.Substring(i + 1).Trim();
            if (k.Equals("Endpoint", StringComparison.OrdinalIgnoreCase)) ep = v.TrimEnd('/');
            else if (k.Equals("AccessKey", StringComparison.OrdinalIgnoreCase)) key = v;
        }
        _endpoint = ep;
        _key = string.IsNullOrEmpty(key) ? Array.Empty<byte>() : global::System.Text.Encoding.UTF8.GetBytes(key);
        _configured = ep.Length > 0 && _key.Length > 0;
        if (!_configured) _log.LogWarning("SignalR not configured (AzureSignalRConnectionString missing/invalid).");
    }

    // ---- token generation (HS256 over the AccessKey) ----
    private string Token(string audience, string? userId, TimeSpan life)
    {
        var creds = new SigningCredentials(new SymmetricSecurityKey(_key), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>();
        if (!string.IsNullOrEmpty(userId)) claims.Add(new Claim(ClaimTypes.NameIdentifier, userId));
        var jwt = new JwtSecurityToken(
            issuer: null, audience: audience, claims: claims,
            expires: DateTime.UtcNow.Add(life), signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    public (string url, string accessToken) BuildClientNegotiate(string hub, string userId)
    {
        // client connects to {endpoint}/client/?hub={hub}
        var clientUrl = $"{_endpoint}/client/?hub={hub.ToLowerInvariant()}";
        var token = Token(clientUrl, userId, TimeSpan.FromMinutes(60));
        return (clientUrl, token);
    }

    // ---- REST management calls ----
    private async Task PostAsync(string apiPath, object? body, CancellationToken ct)
    {
        if (!_configured) return;
        var url = $"{_endpoint}/api/v1/hubs/{apiPath}";
        var token = Token(url, null, TimeSpan.FromMinutes(5));
        var client = _http.CreateClient();
        using var msg = new HttpRequestMessage(HttpMethod.Post, url);
        msg.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        msg.Content = body is null ? null : JsonContent.Create(body);
        try
        {
            var resp = await client.SendAsync(msg, ct);
            if (!resp.IsSuccessStatusCode)
                _log.LogWarning("SignalR REST {Path} -> {Code}", apiPath, (int)resp.StatusCode);
        }
        catch (Exception ex) { _log.LogWarning(ex, "SignalR REST {Path} failed.", apiPath); }
    }

    private async Task PutAsync(string apiPath, CancellationToken ct)
    {
        if (!_configured) return;
        var url = $"{_endpoint}/api/v1/hubs/{apiPath}";
        var token = Token(url, null, TimeSpan.FromMinutes(5));
        var client = _http.CreateClient();
        using var msg = new HttpRequestMessage(HttpMethod.Put, url);
        msg.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        try { await client.SendAsync(msg, ct); }
        catch (Exception ex) { _log.LogWarning(ex, "SignalR REST PUT {Path} failed.", apiPath); }
    }

    public Task AddUserToGroupAsync(string hub, string userId, string group, CancellationToken ct)
        => PutAsync($"{hub.ToLowerInvariant()}/groups/{Uri.EscapeDataString(group)}/users/{Uri.EscapeDataString(userId)}", ct);

    public Task SendToGroupAsync(string hub, string group, object envelope, CancellationToken ct)
        => PostAsync($"{hub.ToLowerInvariant()}/groups/{Uri.EscapeDataString(group)}:send",
                     new { target = Target, arguments = new[] { envelope } }, ct);

    public Task SendToUserAsync(string hub, string userId, object envelope, CancellationToken ct)
        => PostAsync($"{hub.ToLowerInvariant()}/users/{Uri.EscapeDataString(userId)}:send",
                     new { target = Target, arguments = new[] { envelope } }, ct);

    public Task BroadcastAsync(string hub, object envelope, CancellationToken ct)
        => PostAsync($"{hub.ToLowerInvariant()}:send",
                     new { target = Target, arguments = new[] { envelope } }, ct);
}
