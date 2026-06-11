// ============================================================
// FILE        : PinService.cs
// STATUS      : Phase 2 — local acknowledgment PIN (4-digit)
// LAST UPD    : 2026-06-03 23:00 EST
// PURPOSE     : Local mode-escalation PIN. Per the updated decision:
//                 - 4 numeric digits (was 8), displayed XX-XX
//                 - generated locally via CSPRNG at provision
//                 - the PIN is a FORCED ACKNOWLEDGMENT, not a secret:
//                   its job is to make the operator actively confirm
//                   they are enabling a self-automating feature
//                   (Dragon/Phoenix), so nobody can claim "I didn't
//                   know it was on." It is not a security boundary.
//                 - server never sees the PIN
//                 - because it is an acknowledgment (not a secret),
//                   a licensed operator may RETRIEVE it any time via
//                   the Tray "Get PIN" action — not one-time. The
//                   plaintext is persisted in the DPAPI-encrypted
//                   identity blob so it can be revealed repeatedly.
// OWNS        : PIN lifecycle (generate, hash, reveal).
// CALLED BY   : GatewayWorker (generate), TrayIpcServer (reveal).
// ============================================================

using System.Security.Cryptography;
using System.Text;

namespace BEVGateway.Service.System;

public interface IPinService
{
    // Generates a fresh PIN, returns (plaintext, salt, hash). The
    // caller persists salt+hash in the identity blob and holds the
    // plaintext only long enough to stage it for one-time reveal.
    (string plaintext, string saltB64, string hashB64) Generate();

    // Stage the freshly-generated plaintext for a single Tray read.
    void StageForReveal(string plaintext);

    // Returns the staged plaintext exactly once, then clears it.
    // Returns null if nothing staged or already consumed.
    string? RevealOnce();

    // Verify an entered PIN against a stored salt+hash. For the
    // future NEXUS local verification channel.
    bool Verify(string entered, string saltB64, string hashB64);
}

public sealed class PinService : IPinService
{
    private readonly object _lock = new();
    private string? _stagedPlaintext;

    public (string plaintext, string saltB64, string hashB64) Generate()
    {
        // 4 numeric digits, uniformly distributed via a wide random
        // draw (bias negligible at 10^4). The PIN is an acknowledgment,
        // not a secret, so 4 digits is the agreed length.
        var digits = new char[4];
        Span<byte> buf = stackalloc byte[4];
        RandomNumberGenerator.Fill(buf);
        for (int i = 0; i < 4; i++)
            digits[i] = (char)('0' + (buf[i] % 10));
        var plaintext = new string(digits);

        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Hash(plaintext, salt);

        return (plaintext, Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    public void StageForReveal(string plaintext)
    {
        lock (_lock) { _stagedPlaintext = plaintext; }
    }

    public string? RevealOnce()
    {
        lock (_lock)
        {
            var p = _stagedPlaintext;
            _stagedPlaintext = null; // consumed
            return p;
        }
    }

    public bool Verify(string entered, string saltB64, string hashB64)
    {
        if (string.IsNullOrEmpty(saltB64) || string.IsNullOrEmpty(hashB64)) return false;
        try
        {
            var salt = Convert.FromBase64String(saltB64);
            var expected = Convert.FromBase64String(hashB64);
            var actual = Hash(NormalizeEntry(entered), salt);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch { return false; }
    }

    // Display form is XXXX-XXXX; verification normalizes to bare digits.
    public static string NormalizeEntry(string raw) =>
        new string((raw ?? "").Where(char.IsDigit).ToArray());

    public static string ToDisplay(string plaintext) =>
        plaintext.Length == 4 ? $"{plaintext.Substring(0, 2)}-{plaintext.Substring(2, 2)}" : plaintext;

    private static byte[] Hash(string pin, byte[] salt)
    {
        // PBKDF2 / SHA-256, 100k iterations. Overkill for an 8-digit
        // local secret with no online attack surface, but cheap and
        // future-proof.
        using var kdf = new Rfc2898DeriveBytes(
            Encoding.UTF8.GetBytes(pin), salt, 100_000, HashAlgorithmName.SHA256);
        return kdf.GetBytes(32);
    }
}
