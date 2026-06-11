// ============================================================
// FILE        : BEV Hive Jwt Validator.cs
// STATUS      : Phase 1b — Hive /v1/seven/query stub
// LAST UPD    : 2026-05-24 13:00 CST
// PURPOSE     : Local JWT validation against the shared signing
//               key in Key Vault (server-jwt-signing-key). No
//               remote call to Server per request. Returns the
//               claims Hive cares about (tenant_id, machine_id,
//               tier, kate_cap, dragon_tier_max).
// OWNS        : Token validation.
// CALLED BY   : SevenQueryFunction (and future Hive endpoints).
// CHANGE LOG  :
//   2026-05-24 13:00 CST  v0-26.0524-B  Initial scaffold (Phase 1b).
// ============================================================

using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;

namespace BEV.Hive.Services;

public sealed record ValidatedClaims(
    bool   Valid,
    string TenantId,
    string MachineId,
    string Tier,
    int    DragonTierMax,
    bool   KateCap,
    long   Exp,
    string? Error,
    string Role = "",                 // "dashboard" for web-login tokens, "" for cubes
    string Subject = "",              // username for dashboard tokens
    IReadOnlyList<string>? Tenants = null);  // tenants the operator may view

public interface IJwtValidator
{
    Task<ValidatedClaims> ValidateAsync(string token, CancellationToken ct);
}

public sealed class JwtValidator : IJwtValidator
{
    private readonly SecretClient _vault;
    private readonly ILogger<JwtValidator> _log;
    private byte[]? _cachedKey;
    private DateTime _cachedKeyExpiresUtc = DateTime.MinValue;
    private readonly SemaphoreSlim _keyLock = new(1, 1);

    public JwtValidator(SecretClient vault, ILogger<JwtValidator> log)
    {
        _vault = vault;
        _log = log;
    }

    private async Task<byte[]> GetSigningKeyAsync(CancellationToken ct)
    {
        if (_cachedKey is not null && DateTime.UtcNow < _cachedKeyExpiresUtc)
            return _cachedKey;

        await _keyLock.WaitAsync(ct);
        try
        {
            if (_cachedKey is not null && DateTime.UtcNow < _cachedKeyExpiresUtc)
                return _cachedKey;

            var name = Environment.GetEnvironmentVariable("JWT_SIGNING_KEY_SECRET_NAME")
                ?? "server-jwt-signing-key";

            var resp = await _vault.GetSecretAsync(name, cancellationToken: ct);
            var material = resp.Value.Value;
            if (string.IsNullOrEmpty(material))
                throw new InvalidOperationException($"JWT signing key '{name}' is empty.");

            _cachedKey = Convert.FromBase64String(material);
            _cachedKeyExpiresUtc = DateTime.UtcNow.AddHours(1);
            return _cachedKey;
        }
        finally { _keyLock.Release(); }
    }

    public async Task<ValidatedClaims> ValidateAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token))
            return new ValidatedClaims(false, "", "", "", 0, false, 0, "EMPTY_TOKEN");

        try
        {
            var key = await GetSigningKeyAsync(ct);
            var handler = new JwtSecurityTokenHandler();
            var parameters = new TokenValidationParameters
            {
                ValidateIssuer           = true,
                ValidIssuer              = Environment.GetEnvironmentVariable("JWT_ISSUER")   ?? "bev-server",
                ValidateAudience         = true,
                ValidAudience            = Environment.GetEnvironmentVariable("JWT_AUDIENCE") ?? "bev-platform",
                ValidateLifetime         = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey         = new SymmetricSecurityKey(key),
                ClockSkew                = TimeSpan.FromMinutes(1)
            };

            handler.ValidateToken(token, parameters, out var validated);
            var jwt = (JwtSecurityToken)validated;

            var tid = jwt.Claims.FirstOrDefault(c => c.Type == "tid")?.Value ?? "";
            var mid = jwt.Claims.FirstOrDefault(c => c.Type == "mid")?.Value ?? "";
            var tier = jwt.Claims.FirstOrDefault(c => c.Type == "tier")?.Value ?? "";
            var dtm = int.TryParse(jwt.Claims.FirstOrDefault(c => c.Type == "dtm")?.Value, out var d) ? d : 0;
            var kc = jwt.Claims.FirstOrDefault(c => c.Type == "kc")?.Value == "1";
            var exp = ((DateTimeOffset)jwt.ValidTo).ToUnixTimeSeconds();

            // dashboard-user claims (absent on cube tokens — default harmlessly)
            var role = jwt.Claims.FirstOrDefault(c => c.Type == "role")?.Value ?? "";
            var sub  = jwt.Claims.FirstOrDefault(c => c.Type == "sub")?.Value ?? "";
            var tenants = jwt.Claims.Where(c => c.Type == "tenants")
                                    .Select(c => c.Value)
                                    .SelectMany(v => v.Contains(',') ? v.Split(',') : new[] { v })
                                    .Select(s => s.Trim())
                                    .Where(s => s.Length > 0)
                                    .ToList();

            return new ValidatedClaims(true, tid, mid, tier, dtm, kc, exp, null, role, sub, tenants);
        }
        catch (SecurityTokenExpiredException)
        {
            return new ValidatedClaims(false, "", "", "", 0, false, 0, "EXPIRED");
        }
        catch (SecurityTokenException ex)
        {
            _log.LogWarning("JWT validation failed: {Msg}", ex.Message);
            return new ValidatedClaims(false, "", "", "", 0, false, 0, "INVALID_SIGNATURE");
        }
    }
}
