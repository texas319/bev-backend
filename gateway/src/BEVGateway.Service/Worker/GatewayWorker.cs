// ============================================================
// FILE        : GatewayWorker.cs
// STATUS      : Phase 1c-2 — Gateway + Tray binary
// LAST UPD    : 2026-05-27 15:00 CST
// PURPOSE     : The brain. State machine for the Gateway:
//                 1. Wait for provision (identity file OR pending
//                    provision file dropped by setup wizard)
//                 2. Provision against Server → get JWT
//                 3. Register MID via tenant/mids
//                 4. Loop: push HUD every 15s, poll commands
//                    in parallel, refresh JWT when needed,
//                    handle commands as they arrive
// OWNS        : Lifecycle, network loops, command dispatch.
// CALLED BY   : Service host as IHostedService.
// ============================================================

using BEVGateway.Service.Net;
using BEVGateway.Service.Storage;
using BEVGateway.Service.System;
using BEVGateway.Shared;
using BEVGateway.Shared.Wire;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BEVGateway.Service.Worker;

public sealed class GatewayWorker : BackgroundService
{
    private readonly IIdentityStore _store;
    private readonly IFingerprintService _fp;
    private readonly INodeClassDetector _nodeClass;
    private readonly IPinService _pin;
    private readonly IServerClient _server;
    private readonly IHiveClient _hive;
    private readonly ISystemActions _actions;
    private readonly StatusReporter _status;
    private readonly ILogger<GatewayWorker> _log;

    private PrivateIdentity? _identity;
    private UpdateService? _updater;
    private DateTime _lastCommandsSinceUtc = DateTime.UtcNow.AddMinutes(-5);
    private int _consecutiveHudFailures;
    private const int HudFailuresBeforeDown = 2;
    private DateTime _lastHeartbeatLog = DateTime.MinValue;

    public GatewayWorker(
        IIdentityStore store,
        IFingerprintService fp,
        INodeClassDetector nodeClass,
        IPinService pin,
        IServerClient server,
        IHiveClient hive,
        ISystemActions actions,
        StatusReporter status,
        ILogger<GatewayWorker> log)
    {
        _store = store; _fp = fp; _nodeClass = nodeClass; _pin = pin;
        _server = server; _hive = hive;
        _actions = actions; _status = status; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Gateway worker starting (build={Build}).", GatewayConstants.GatewayBuild);

        // -------- Phase 1: identity acquisition --------
        await EnsureIdentityAsync(stoppingToken);
        if (_identity is null)
        {
            _log.LogWarning("No identity acquired during startup; will keep polling for setup completion.");
            // Don't exit. The wizard might still write the pending file
            // after the service starts. Continue with periodic checks.
        }

        // -------- Phase 2: main loop --------
        var hudTimer = new PeriodicTimer(GatewayConstants.HudInterval);

        // Command poll runs in parallel so it doesn't block HUD cadence.
        var commandLoop = Task.Run(() => CommandLoop(stoppingToken), stoppingToken);

        // Audit shipper runs in parallel too — tails the EAGLE audit CSV
        // directory and POSTs complete files to Hive. It pulls the live
        // bearer/mid each cycle via the accessor so JWT refreshes are
        // picked up automatically. Never blocks HUD or commands.
        var auditShipper = new AuditShipper(_hive, _log, _status);
        var auditLoop = Task.Run(() => auditShipper.RunAsync(
            () => _identity is null ? null : ((string, string)?)(_identity.BearerToken, _identity.MachineId),
            stoppingToken), stoppingToken);

        // Auto-update: checks the Server manifest for a newer Gateway
        // build and self-installs. Startup check + hourly; also reachable
        // out-of-band via the UPDATE_GATEWAY command. Parallel; never
        // blocks HUD/commands.
        _updater = new UpdateService(_server, _status, _log);
        var updateLoop = Task.Run(() => _updater.RunAsync(
            () => _identity is null ? null : ((string, string)?)(_identity.BearerToken, _identity.MachineId),
            stoppingToken), stoppingToken);

        // BEV LiveLink: read EAGLE's per-instance Relay\FLEET\HUD.*.json
        // and forward real live state to Hive for FleetView. Replaces the
        // stubbed HUD content. Parallel; never blocks HUD/commands/audit.
        var liveReader = new LiveLinkReader(_hive, _log, _status);
        var liveLoop = Task.Run(() => liveReader.RunAsync(
            () => _identity is null ? null : ((string, string)?)(_identity.BearerToken, _identity.MachineId),
            stoppingToken), stoppingToken);

        try
        {
            while (await hudTimer.WaitForNextTickAsync(stoppingToken))
            {
                if (_identity is null)
                {
                    await TryProvisionFromPendingAsync(stoppingToken);
                    continue;
                }
                await EnsureFreshTokenAsync(stoppingToken);
                await PushHudSnapshotAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        finally
        {
            try { await commandLoop; } catch { }
            try { await auditLoop; } catch { }
            try { await updateLoop; } catch { }
            try { await liveLoop; } catch { }
            _log.LogInformation("Gateway worker stopped.");
        }
    }

    // -------- Identity acquisition --------

    private async Task EnsureIdentityAsync(CancellationToken ct)
    {
        if (_store.Exists())
        {
            _identity = await _store.LoadAsync(ct);
            if (_identity is not null)
            {
                // Uniform node class: everyone is CUBE. Re-stamp any older
                // identity that was provisioned as SPHERE so the change
                // reaches boxes that provisioned before this decision.
                if (_identity.NodeClass != GatewayConstants.NodeClassCube)
                {
                    _log.LogInformation("Normalizing node class {Old} -> CUBE.", _identity.NodeClass);
                    _identity.NodeClass = GatewayConstants.NodeClassCube;
                    try { await _store.SaveAsync(_identity, ct); } catch (Exception ex) { _log.LogWarning(ex, "node-class re-stamp save failed"); }
                }
                _log.LogInformation("Loaded identity: tenant={Tenant} mid={Mid} tier={Tier}",
                    _identity.TenantId, _identity.MachineId, _identity.Tier);
                ReflectIdentityToStatus();
                await _store.WriteNexusDropAsync(_identity, ct);
                return;
            }
        }

        // No saved identity — try a pending provision file from the wizard.
        await TryProvisionFromPendingAsync(ct);
    }

    private async Task TryProvisionFromPendingAsync(CancellationToken ct)
    {
        var pending = await _store.ReadPendingProvisionAsync(ct);
        if (pending is null) return;

        _log.LogInformation("Pending provision found for {Email}. Attempting provision...", pending.Email);

        var fingerprint = _fp.Compute();
        var resp = await _server.ProvisionAsync(
            pending.Email, pending.LicenseKey, fingerprint,
            GatewayConstants.GatewayBuild, ct);

        if (!resp.Ok)
        {
            _log.LogError("Provision failed: {Error}", resp.Error);
            _status.Update(s =>
            {
                s.Provisioned = false;
                s.Error = $"Provision failed: {resp.Error}";
            });
            return; // Leave pending file for retry / setup wizard reattempt.
        }

        var identity = new PrivateIdentity
        {
            Email         = pending.Email,
            LicenseKey    = pending.LicenseKey,
            TenantId      = resp.TenantId ?? "",
            MachineId     = resp.MachineId ?? "",
            Fingerprint   = fingerprint,
            Hostname      = Environment.MachineName,
            Tier          = resp.Tier ?? "",
            DragonTierMax = resp.DragonTierMax ?? 0,
            NodeClass     = _nodeClass.Detect(),
            BearerToken   = resp.BearerToken ?? "",
            TokenExpUtc   = resp.ExpiresUtc ?? "",
            BoundUtc      = resp.BoundUtc ?? DateTime.UtcNow.ToString("o"),
            LastProvisionUtc = DateTime.UtcNow.ToString("o")
        };

        // Generate the local acknowledgment PIN once, at first provision.
        // Persist the salted hash AND the plaintext (the PIN is an
        // acknowledgment, not a secret, and the whole identity blob is
        // DPAPI-encrypted at rest). Also stage for the completion-screen
        // reveal. The operator can retrieve it any time via "Get PIN".
        if (string.IsNullOrEmpty(identity.PinHash))
        {
            var (pinPlain, salt, hash) = _pin.Generate();
            identity.PinSalt   = salt;
            identity.PinHash   = hash;
            identity.PinPlain  = pinPlain;
            identity.PinSetUtc = DateTime.UtcNow.ToString("o");
            _pin.StageForReveal(pinPlain);
            _log.LogInformation("Generated local acknowledgment PIN (4-digit; hash+plaintext stored in DPAPI identity).");
        }

        // Register MID metadata.
        var regResp = await _server.RegisterMidAsync(
            identity.BearerToken, identity.MachineId, identity.Hostname,
            identity.Email, ct);

        if (regResp.Registered)
        {
            identity.FleetRole = regResp.FleetRole ?? "primary";
        }
        else
        {
            _log.LogWarning("Tenant/mids registration soft-failed: {Error}", regResp.Error);
            identity.FleetRole = "primary"; // default; retried on next service start
        }

        await _store.SaveAsync(identity, ct);
        await _store.WriteNexusDropAsync(identity, ct);
        await _store.ClearPendingProvisionAsync(ct);
        _identity = identity;
        ReflectIdentityToStatus();
        _log.LogInformation("Provision complete: tenant={Tenant} mid={Mid} tier={Tier}",
            identity.TenantId, identity.MachineId, identity.Tier);
    }

    // -------- JWT refresh --------

    private async Task EnsureFreshTokenAsync(CancellationToken ct)
    {
        if (_identity is null) return;
        if (!DateTime.TryParse(_identity.TokenExpUtc, out var exp))
        {
            await ForceReprovisionAsync(ct);
            return;
        }
        var minutesLeft = (int)(exp - DateTime.UtcNow).TotalMinutes;
        _status.Update(s => { s.TokenMinutesLeft = Math.Max(0, minutesLeft); s.TokenExpUtc = _identity.TokenExpUtc; });

        if (minutesLeft > GatewayConstants.JwtRefreshLead.TotalMinutes) return;

        _log.LogInformation("Refreshing JWT (minutes left={MinutesLeft})", minutesLeft);
        await ForceReprovisionAsync(ct);
    }

    private async Task ForceReprovisionAsync(CancellationToken ct)
    {
        if (_identity is null) return;
        // Hardened: this method must NEVER throw out to the background
        // service host. An unhandled exception here (e.g. an identity
        // file write collision) was crashing the whole service under
        // StopHost behavior. Any failure is logged and swallowed; the
        // worker keeps running on the existing token.
        try
        {
            var resp = await _server.ProvisionAsync(
                _identity.Email, _identity.LicenseKey, _identity.Fingerprint,
                GatewayConstants.GatewayBuild, ct);
            if (!resp.Ok)
            {
                _log.LogWarning("Reprovision failed: {Error}", resp.Error);
                _status.Update(s => { s.ServerUp = false; s.Error = $"Reprovision: {resp.Error}"; });
                return;
            }
            _identity.BearerToken = resp.BearerToken ?? _identity.BearerToken;
            _identity.TokenExpUtc = resp.ExpiresUtc ?? _identity.TokenExpUtc;
            _identity.LastProvisionUtc = DateTime.UtcNow.ToString("o");
            _identity.Tier = resp.Tier ?? _identity.Tier;
            _identity.DragonTierMax = resp.DragonTierMax ?? _identity.DragonTierMax;
            await _store.SaveAsync(_identity, ct);
            await _store.WriteNexusDropAsync(_identity, ct);
            ReflectIdentityToStatus();
            _status.Update(s => { s.ServerUp = true; s.Error = null; });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ForceReprovision threw - swallowed to keep host alive");
            _status.Update(s => { s.Error = $"Reprovision error: {ex.Message}"; });
        }
    }

    // -------- HUD push --------

    private async Task PushHudSnapshotAsync(CancellationToken ct)
    {
        if (_identity is null) return;

        var payload = BuildStubHud();
        var (ok, body, error) = await _hive.PushHudAsync(_identity.BearerToken, _identity.MachineId, payload, ct);

        var nowIso = DateTime.UtcNow.ToString("o");
        if (ok)
        {
            _consecutiveHudFailures = 0;
            _status.Update(s =>
            {
                s.HiveUp = true;
                s.ServerUp = true;
                s.LastHudUtc = nowIso;
                s.LastHudStatus = "ok";
                s.Error = null;
            });

            // Build-stamped heartbeat, ~once/minute, so ANY slice of the log
            // identifies the build that produced it (not just the startup
            // banner). Throttled to avoid flooding (HUD pushes every 15s).
            var now = DateTime.UtcNow;
            if ((now - _lastHeartbeatLog).TotalSeconds >= 60)
            {
                _lastHeartbeatLog = now;
                _log.LogInformation(
                    "Heartbeat build={Build} mid={Mid} hive=up server=up jwtMinLeft={Jwt}",
                    GatewayConstants.GatewayBuild, _identity.MachineId,
                    _status.Get().TokenMinutesLeft);
            }
        }
        else
        {
            // Hysteresis: a single dropped HUD push (transient network blip,
            // Hive cold-start, etc.) must not flip HiveUp false and blink the
            // tray. Only declare Hive down after several consecutive misses.
            // The HUD push (15s request/response) is the SOLE authoritative
            // owner of HiveUp — the command long-poll no longer touches it.
            _consecutiveHudFailures++;
            if (_consecutiveHudFailures >= HudFailuresBeforeDown)
            {
                _status.Update(s =>
                {
                    s.HiveUp = false;
                    s.LastHudStatus = $"fail: {error}";
                    s.Error = $"HUD: {error}";
                });
            }
            // NOTE: A Hive auth rejection (401) means Hive will not accept
            // our Server-issued token - it does NOT mean our identity is
            // invalid. We must NOT force-reprovision here: that spun an
            // endless provision loop (each cycle burned a seat) and the
            // identity-file write collided with itself and crashed the
            // host. Hive being down is non-fatal: trading never blocks on it.
        }
    }

    private HudSnapshotRequest BuildStubHud()
    {
        // Real HUD ingestion (reading EAGLE's HUD.json) lands in Phase 1e.
        // For 1c-2 we publish a stub so Hive sees regular heartbeats.
        return new HudSnapshotRequest
        {
            Mid          = _identity!.MachineId,
            WallUtc      = DateTime.UtcNow.ToString("o"),
            BuildLabel   = GatewayConstants.GatewayBuild,
            Nt8State     = "unknown",
            NexusLoaded  = false,
            Strategies   = new List<HudStrategy>(),
            Amc          = new HudAmc { ReplicationState = "ready" },
            Feeds        = new HudFeeds { L1 = false, L2 = false, HiveSeven = true },
            AuditTail    = new HudAuditTail()
        };
    }

    // -------- Command poll loop --------

    private async Task CommandLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_identity is null || string.IsNullOrEmpty(_identity.BearerToken))
            {
                await Task.Delay(GatewayConstants.StartupRetryDelay, ct);
                continue;
            }

            var (ok, body, error) = await _hive.PollCommandsAsync(
                _identity.BearerToken, _identity.MachineId, _lastCommandsSinceUtc, ct);

            if (!ok)
            {
                // IMPORTANT: do NOT write HiveUp here. The command poll is a
                // 30s long-poll; a cycle returning without commands (or a
                // transient blip on this one call) is NOT a Hive-health
                // signal and must not toggle the health flag. HiveUp is
                // owned SOLELY by the HUD-push loop (a simple 15s
                // request/response), which is the authoritative heartbeat.
                // Previously both loops wrote HiveUp on mismatched timers,
                // so the flag thrashed every ~30s and the tray blinked.
                // Back off and retry; Hive-down is non-fatal to trading.
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                continue;
            }

            if (!string.IsNullOrEmpty(body?.NextSinceUtc) &&
                DateTime.TryParse(body.NextSinceUtc, out var next))
            {
                _lastCommandsSinceUtc = next;
            }

            if (body?.Commands is { Count: > 0 })
            {
                foreach (var cmd in body.Commands)
                {
                    await DispatchCommandAsync(cmd, ct);
                }
            }
        }
    }

    private async Task DispatchCommandAsync(GatewayCommand cmd, CancellationToken ct)
    {
        if (_identity is null) return;
        _log.LogInformation("Command received: id={Id} kind={Kind}", cmd.CommandId, cmd.Kind);
        _status.Update(s => { s.LastCommandUtc = DateTime.UtcNow.ToString("o"); });

        var executedUtc = DateTime.UtcNow.ToString("o");
        (bool ok, string detail) result = cmd.Kind switch
        {
            "PING"                => (true, "Pong."),
            "REFRESH_CREDENTIALS" => await RefreshCredentialsAsync(ct),
            "RESTART_NT8"         => await _actions.RestartNt8Async(ct),
            "REBOOT_BOX"          => await _actions.RebootBoxAsync(ct),
            "KILL_ALL"            => await _actions.WriteKillAllFlagAsync(ct),
            "INVOKE"              => await DispatchInvokeAsync(cmd, ct),
            "UPDATE_GATEWAY"      => await CheckForUpdateAsync(ct),
            _                     => (false, $"Unknown command kind: {cmd.Kind}")
        };

        var ack = new CommandAckRequest
        {
            CommandId   = cmd.CommandId,
            Result      = result.ok ? "success" : "failed",
            Detail      = result.detail,
            ExecutedUtc = executedUtc
        };
        var (ackOk, ackError) = await _hive.AckCommandAsync(_identity.BearerToken, _identity.MachineId, ack, ct);
        if (!ackOk) _log.LogWarning("Ack failed: {Error}", ackError);
    }

    private async Task<(bool ok, string detail)> DispatchInvokeAsync(GatewayCommand cmd, CancellationToken ct)
    {
        // INVOKE args were set by Hive: { function_id, args (raw json string), actor }.
        if (cmd.Args is null) return (false, "INVOKE missing args.");
        string functionId = ArgStr(cmd.Args, "function_id");
        string argsJson   = ArgStr(cmd.Args, "args");
        string actor      = ArgStr(cmd.Args, "actor");
        if (string.IsNullOrWhiteSpace(functionId)) return (false, "INVOKE missing function_id.");
        _log.LogInformation("INVOKE dispatch fn={Fn} actor={Actor} req={Req}", functionId, actor, cmd.CommandId);
        return await _actions.WriteInvokeAsync(functionId, argsJson, actor, cmd.CommandId, ct);
    }

    private static string ArgStr(Dictionary<string, object> args, string key)
    {
        if (!args.TryGetValue(key, out var v) || v is null) return "";
        // values arrive as JsonElement (System.Text.Json) or plain string
        if (v is global::System.Text.Json.JsonElement je)
            return je.ValueKind == global::System.Text.Json.JsonValueKind.String ? (je.GetString() ?? "")
                 : je.GetRawText();
        return v.ToString() ?? "";
    }

    private async Task<(bool ok, string detail)> CheckForUpdateAsync(CancellationToken ct)
    {
        if (_updater is null) return (false, "Updater not initialized.");
        return await _updater.CheckNowAsync(
            () => _identity is null ? null : ((string, string)?)(_identity.BearerToken, _identity.MachineId),
            ct);
    }

    private async Task<(bool ok, string detail)> RefreshCredentialsAsync(CancellationToken ct)
    {
        // Sprint 1: REFRESH_CREDENTIALS reprovisions the JWT.
        // Phase 1d will extend this to also pull /v1/credentials/gemini
        // when that endpoint ships.
        await ForceReprovisionAsync(ct);
        return (true, "JWT refreshed.");
    }

    // -------- Status mirror --------

    private void ReflectIdentityToStatus()
    {
        if (_identity is null) return;
        _status.Update(s =>
        {
            s.Provisioned = true;
            s.TenantId    = _identity.TenantId;
            s.MachineId   = _identity.MachineId;
            s.Tier        = _identity.Tier;
            s.NodeClass   = _identity.NodeClass;
            s.FleetRole   = _identity.FleetRole;
            s.TokenExpUtc = _identity.TokenExpUtc;
            if (DateTime.TryParse(_identity.TokenExpUtc, out var exp))
                s.TokenMinutesLeft = Math.Max(0, (int)(exp - DateTime.UtcNow).TotalMinutes);
        });
    }
}
