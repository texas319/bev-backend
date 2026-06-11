// ─────────────────────────────────────────────────────────────
// BUILD: GW.0610.26-A
// DATE : 2026-06-10
// ─────────────────────────────────────────────────────────────
//   CUBE-IDENTITY DROP now carries cube_tag. The Gateway already owns
//     the authoritative MID and already drops cube-identity.json (MID +
//     tenant + tier) into …\NinjaTrader 8\BEV\Gateway\ for every user
//     profile, on identity load/provision/refresh — NEXUS reads it for
//     its header MID instead of self-computing. Added cube_tag to that
//     drop (PublicIdentity.CubeTag <- ReadCubeTag()) so NEXUS also gets
//     the vanity tag for the C-XXXXXX // CUBE_TAG fleet section headers
//     from the same single source. One authoritative MID + tag, owned by
//     the Gateway, read by NEXUS — no IPC, no self-compute drift.
//   NOTE — EAGLE->Hive correlation is already canonical: LiveLinkReader
//     stamps the Gateway's bound MID onto every forwarded FLEET snapshot,
//     ignoring EAGLE's self-computed machine_id. The Hive sections by the
//     bound MID already; this drop fixes the remaining NEXUS-header side.
//   License fingerprint stays separate (machine-bound, self-computed,
//     PART 109) — unaffected; the drop is fleet identity only.
//   MSI -> 1.0.10.0; label GW.0610.26-A. (Carries S: silent + cube tag +
//     blink guard + everyone-CUBE + INVOKE.)
//
// ─────────────────────────────────────────────────────────────
// BUILD: GW.0607.26-S
// DATE : 2026-06-07
// ─────────────────────────────────────────────────────────────
//   SILENT GATEWAY — removed the last Windows balloon-toast path. The
//     disconnect/reconnect popup + system ping are gone; ShowBalloon now
//     writes silently to the tray status line, double-click opens the
//     windowed status dialog. No audio, no popup, ever.
//   CUBE TAG — vanity VPS label (C-XXXXXX // TAG in the terminal header).
//     Stored in ProgramData\NexusGateway\cube-tag.txt (NOT a DB row;
//     MID is the serial, tag is a friendly name). Stamped live into each
//     BevLiveSnapshot (cube_tag) -> flows through Hive roster frame to the
//     header. Set at install or via the new "Set VPS Tag…" tray item.
//   (Carries R: everyone-CUBE + INVOKE handler.) MSI -> 1.0.9.0; label
//     GW.0607.26-S.
//

// ─────────────────────────────────────────────────────────────
// BUILD: GW.0606.26-R
// DATE : 2026-06-06
// ─────────────────────────────────────────────────────────────
//   CHANGE — uniform node class: EVERY node is CUBE. NodeClassDetector
//     now always returns CUBE (CPU still read for the log line only),
//     and GatewayWorker re-stamps CUBE onto any already-provisioned
//     identity on startup (covers boxes that provisioned as SPHERE
//     before this decision) + rewrites the NEXUS drop so the terminal
//     reads CUBE too.
//   (Carries forward Q: INVOKE control-plane handler + relay command.json
//    writer. MSI -> 1.0.8.0 so Windows Installer accepts the in-place
//    upgrade; label GW.0606.26-R.)
//

// ─────────────────────────────────────────────────────────────
// BUILD: GW.0606.26-Q
// DATE : 2026-06-06
// PHASE: 2 / Drop 2 — control-plane execution (INVOKE handler)
// ─────────────────────────────────────────────────────────────
//   ADD — INVOKE command kind. Hive /v1/invoke routes a control-plane
//     function as an INVOKE command; the Gateway drains it, extracts
//     function_id + args + actor, and writes command.json into each
//     per-box Relay dir (atomic temp-then-move) for EAGLE (PART 36/37)
//     to poll and execute. request_id (command_id) correlates the
//     result back through the audit log.
//   ADD — GatewayConstants.DiscoverRelayDirs() + RelayRelDir, mirroring
//     the LiveLink dir discovery, so command.json reaches the trading
//     user's relay even when the service runs as another user.
//   ADD — SystemActions.WriteInvokeAsync; ISystemActions extended.
//   VERSION: GatewayBuild -> GW.0606.26-Q.
//

// ─────────────────────────────────────────────────────────────
// BUILD: GW.0527.26-B  (Build_052726-Gateway-NX-B.zip)
// DATE : 2026-05-28 11:30 CST
// PHASE: 1c-2 — PIN subsystem + CPU node class + brand scrub
// ─────────────────────────────────────────────────────────────
//   NEW — LOCAL MODE-ESCALATION PIN (per NEXUS PIN memo 2026-05-27)
//     • Service/System/PinService.cs: CSPRNG 8-digit PIN,
//       displayed XXXX-XXXX. PBKDF2-SHA256 (100k iters, 16-byte
//       salt) hash is the ONLY persisted form — plaintext never
//       touches disk. Generated once at first provision.
//     • Plaintext staged in memory for a single reveal; Tray
//       fetches it via new GET_PIN_ONCE IPC command exactly once,
//       then the Service clears it. No re-display path (matches
//       the memo's shown-once rule). Regenerate lives NEXUS-side
//       (Settings -> Security) — NOT built here.
//     • PinHash + PinSalt + PinSetUtc added to PrivateIdentity
//       (DPAPI blob). Server NEVER sees the PIN or its hash —
//       /v1/provision is untouched.
//     • Completion screen shows NODE CLASS / TIER / MID / PIN as
//       NEXUS crumbs, with the "Write this down. It won't be shown
//       again. Regenerate anytime from NEXUS Settings -> Security."
//       note. If the one-time reveal is missed, shows a dim dash +
//       regenerate hint instead.
//
//   NEW — CPU-BASED NODE CLASS (per PM 2026-05-28)
//     • Service/System/NodeClassDetector.cs: reads
//       Win32_Processor.Name. Xeon => CUBE; Core i3/i5/i7/i9,
//       AMD/Ryzen/Athlon/EPYC => SPHERE; unknown => CUBE (safe
//       default). Determined locally at provision; no licensing
//       concept involved.
//     • node_class flows into PrivateIdentity + the NEXUS drop +
//       the status snapshot, replacing the prior placeholder.
//
//   CHANGED — BRAND SCRUB (PM: never reference the enterprise name)
//     • Manufacturer "Kate Capital" -> "BEV Systems" (MSI + Bundle
//       + constants). Registry key Software\BEV Systems\Nexus
//       Gateway.
//     • Removed every kate_cap / KateCap field from the wire,
//       identity, IPC, and status models. The Server may still
//       send kate_cap on /v1/provision; the Gateway ignores it
//       (System.Text.Json drops unknown members).
//     • NexusTheme license-chip colors renamed off the enterprise
//       term (LicenseChipBg/Border/Text); hex values unchanged.
//
//   NOTE — node class is computed but everyone resolves to CUBE in
//   practice today on Xeon VPS boxes; SPHERE appears automatically
//   on consumer-silicon boxes. No server-side node_class needed.
//
// ─────────────────────────────────────────────────────────────
// BUILD: GW.0527.26-B  (Build_052726-Gateway-NX-A.zip)
// DATE : 2026-05-28 01:00 CST
// PHASE: 1c-2 — NEXUS visual retrofit + rename + bootstrapper
// ─────────────────────────────────────────────────────────────
//   RENAME — "BEV Gateway" -> "NEXUS GATEWAY"
//     • Product is now "Nexus Gateway" under the NEXUS family.
//       BEV is the strategy NEXUS consumes; the Gateway is NEXUS
//       infrastructure. (Per NEXUS Visual Language memo 2026-05-27
//       + PM direction 2026-05-28.)
//     • Windows service name: BEVGateway -> NexusGateway
//       (DisplayName "Nexus Gateway"). REQUIRES uninstalling any
//       prior "BEV Gateway" install before installing this one.
//     • ProgramData path: C:\ProgramData\BEVGateway ->
//       C:\ProgramData\NexusGateway.
//     • Named pipe: BEVGateway.tray -> NexusGateway.tray.
//     • NEXUS drop path UNCHANGED: still
//       Documents\NinjaTrader 8\BEV\Gateway\cube-identity.json
//       (that's the contract NEXUS reads — not renamed).
//     • Assembly names (BEVGateway.Service.exe / .Tray.exe) kept
//       to avoid churning the WiX File IDs; only the product,
//       service, and paths rebrand.
//
//   NEW — NEXUS VISUAL LANGUAGE (memo-exact tokens)
//     • Shared/NexusTheme.cs: the canonical palette + fonts,
//       transcribed from PART 21 BEVxNEXUS_Theme. Exact hex:
//       WindowBg #000000, PanelBg #0A0B0E, BorderMid #1F2530,
//       AccentMid #FFA940 (brand amber), AccentHot #FFBE6A,
//       TextBright #E8EBF0, TextDim #5F6772, StatusGreen #52C77C,
//       StatusRed #FF6B6B, AttentionAmb #F0D454, etc. Font
//       resolves IBM Plex Mono -> Cascadia Mono -> Consolas
//       (memo-sanctioned fallback chain; IBM Plex not embedded).
//     • Shared.csproj now UseWindowsForms (theme needs Font/Color).
//
//   NEW — THEMED WINFORMS SURFACES
//     • Tray/NexusForm.cs: borderless window chrome — OS titlebar
//       stripped, 1px BorderMid edge, 28px dark titlebar with the
//       3px amber accent dash + mono title + 38px X close button,
//       draggable by titlebar. (memo Section 4 + 3.2)
//     • Tray/NexusButton.cs: flat bordered button, accent (3.7)
//       and destructive (3.6) variants — transparent fill, amber/
//       red border, inverts to filled-with-black-text on hover.
//     • Tray/SetupWizardForm.cs: fully rethemed first-run wizard.
//       GATEWAY badge in the titlebar (NEX-style amber chip, 3.1).
//       Phase 1 collects email + license; on save it records the
//       NEXUS drop's prior written_utc, writes pending-provision,
//       then POLLS the plaintext drop (up to 45s) for a fresh
//       provision result. Phase 2 (completion) shows NODE CLASS
//       (CUBE), MID, TENANT as crumbs (3.8). PIN row present but
//       shows "assigned on first sync" (parked — see note).
//     • Tray/StatusDialogForm.cs: replaces the plain MessageBox
//       status with a themed window — health heading, crumbs, and
//       green/dim status dots with glow (3.3) for SERVER + HIVE.
//     • Tray/IconFactory.cs: tray dot recolored to NEXUS semantic
//       palette (StatusGreen / AttentionAmb / StatusRed / TextDim).
//
//   NEW — BOOTSTRAPPER (.exe) — fixes the dev-box failure
//     • installer/Bundle.wxs: WiX Burn bundle chaining the .NET 8
//       Desktop Runtime (x64) + the MSI. RegistrySearch detects
//       whether the runtime is already present; the runtime
//       ExePackage installs only if absent. This is why the dev
//       box (no .NET 8 Desktop Runtime) previously failed — the
//       bootstrapper now supplies it.
//     • Custom dark WixStdBA theme: installer/theme.xml +
//       theme.wxl + background.png + nexus-logo.png + nexus.ico,
//       all NEXUS palette (black canvas, amber headers/buttons,
//       Consolas mono). GATEWAY badge baked into the background.
//     • build.ps1 now: installs Util + Bal WiX extensions,
//       downloads the .NET 8 Desktop Runtime into installer\redist
//       at build time, publishes Service + Tray, harvests, builds
//       the MSI, then builds the bootstrapper .exe.
//       SHIP ARTIFACT IS NOW: dist\Nexus-Gateway-Setup.exe
//     • upload-msi.ps1 publishes the .exe (and the bare MSI for
//       reference) to Azure Blob 'installers' container.
//
//   PARKED — PIN
//     • The visual memo's completion screen calls for a PIN
//       (auto-generated, displayed once). That references a
//       separate "Gateway license/PIN memo" we do NOT have, and
//       it needs backend support (register PIN, storage column,
//       display-once flow). The wizard reserves the PIN crumb row
//       ("assigned on first sync") so the layout is ready, but no
//       PIN is generated yet. Wire it when the PIN memo + backend
//       endpoint land.
//
//   NOTE — completion screen placement
//     • The memo put node-class/MID/PIN on the "installer"
//       completion screen, but the MID does not exist until AFTER
//       provisioning (which happens post-install when the operator
//       enters their license). So that panel lives in the setup
//       WIZARD, not the bootstrapper. The bootstrapper success
//       screen just says "setup window will open to finish
//       provisioning."
//
//   DEFERRED (unchanged)
//     • Real HUD ingestion from EAGLE                  — Phase 1e
//     • Audit file tailing (TCA/BARSNAP/SIGEVAL)       — Phase 1e
//     • /v1/credentials/gemini pull on REFRESH         — Phase 1d
//     • PIN backend + display-once                     — pending memo
//     • L2 stream WebSocket client                     — Phase 2
//     • Auto-update mechanism                          — Phase 2
//     • Code-signing the bootstrapper with an EV cert  — when budget allows
//
// ─────────────────────────────────────────────────────────────
// BUILD: GW.0527.26-A  (Build_052726-Gateway-A..H)
// DATE : 2026-05-27 15:00 CST
// PHASE: 1c-2 — Gateway + Tray binary (pre-rename)
// ─────────────────────────────────────────────────────────────
//   First Gateway build. Three-project solution (Shared / Service
//   / Tray), DPAPI identity, WMI fingerprint, provision + register
//   + HUD push loop + command long-poll + JWT refresh, named-pipe
//   Tray IPC, WiX MSI. Iterated A->H fixing PowerShell parser
//   (em-dash), WiX harvest BOM, namespace collision
//   (global::System.Net), XML PI corruption, duplicate Component
//   IDs, Tray/Service shared-file dedup. MSI compiled at build H
//   (1.17 MB, framework-dependent) — but failed to start on boxes
//   lacking the .NET 8 Desktop Runtime, which is what this
//   GW.0527.26-B bootstrapper build fixes.
//
// ─────────────────────────────────────────────────────────────
// BUILD: v0-26.0527-A  (Build_052726-Server-A.zip + Hive-A.zip)
// DATE : 2026-05-27 14:00 CST
// PHASE: 1c-1 — Server + Hive deltas for Gateway lifecycle
// ─────────────────────────────────────────────────────────────
//   Server: /v1/tenant/mids.  Hive: /v1/hud-snapshot, /v1/commands,
//   /v1/command-ack. All deployed + validated end-to-end.
//
// ============================================================

// -------------------------------------------------------------
// BUILD: GW.0602.26-P  (Build_060426-Gateway-NX-P.zip)
// DATE : 2026-06-04 13:00 EST
// -------------------------------------------------------------
//   *** SECOND AUTO-PUSHED BUILD *** — pushed via the channel proven
//   by O (manifest + blob). No hand-install.
//
//   ADD - Trades Assimilated counter. LiveLinkReader pulls the global
//     fleet-wide TCA count from Hive GET /v1/assimilated each cycle
//     (the same number Phoenix/Dragon reason against). Surfaced as
//     "TRADES ASSIM" in the tray status window. Identical on every
//     box (it is platform-wide, not per-box).
//   FIX - MID consistency: the LiveLink reader now stamps the
//     Gateway's BOUND MID on every forwarded snapshot, ignoring
//     EAGLE's payload mid. Settles "one physical box = one identity"
//     in the database. (MID = box, IID = EAGLE instance underneath.)
//   FIX - Removed the "Service unreachable" balloon popup. The tray
//     icon color + status window already show online/offline; the
//     popup was redundant noise.
//   MSI ProductVersion -> 1.0.6.0 (clean MajorUpgrade).
//   VERSION: GatewayBuild -> GW.0602.26-P.
//


//   *** FIRST AUTO-PUSHED BUILD *** — N carried the updater; O is
//   the first build delivered via the update channel (publish MSI to
//   blob + set GATEWAY_UPDATE_* settings; fleet self-installs). No
//   hand-install on the boxes.
//
//   ADD - BEV LiveLink ingestion (replaces the stub HUD content).
//     • LiveLinkReader.cs: parallel loop tails C:\Users\*\...\BEV\
//       Relay\FLEET\HUD.*.json (per EAGLE LiveLink memo), deserializes
//       BevLiveSnapshot, normalizes MID to C-, drops stale files, and
//       POSTs each to Hive /v1/fleet/live for FleetView. Real EAGLE
//       state now flows: regime, prices, position, pnl, trace-mode,
//       per-family stats, families open mask.
//     • BevLiveSnapshot + BevLiveCommand wire models; HiveClient
//       PushLiveAsync.
//   ADD - PIN is now a 4-digit local ACKNOWLEDGMENT (was 8). Plaintext
//     persisted in the DPAPI identity blob so it can be retrieved
//     repeatedly. New repeatable GET_PIN IPC + tray "Get PIN…" menu
//     item (shows XX-XX). Server never sees the PIN.
//   ADD - Tray status window now shows audit-ship activity (last
//     cycle ok/dup/fail, ship total) + LiveLink push count, so fleet
//     health is visible without tailing logs. Shipper + LiveLink
//     report into StatusReporter.
//   MSI ProductVersion -> 1.0.5.0 (clean MajorUpgrade).
//   VERSION: GatewayBuild -> GW.0602.26-O.
//


//   ADD - Auto-update channel. The Gateway now checks the Server for
//   a newer build and self-installs, so future builds no longer need
//   a manual copy + install on every box.
//     • UpdateService.cs: parallel loop (startup check + hourly) that
//       GETs /v1/gateway/manifest, compares the manifest version to
//       its own GatewayBuild (GW.MMDD.YY-X parsed to date+letter; only
//       strictly-newer installs, never a downgrade), downloads the MSI,
//       verifies SHA256, and launches a DETACHED cmd helper that runs
//       msiexec — the MSI's MajorUpgrade then stops/replaces/restarts
//       this service. (A running service can't MSI-upgrade itself
//       in-process, hence the detached helper.)
//     • New UPDATE_GATEWAY command: pushes an immediate update check
//       through the existing command channel (one box or whole fleet).
//     • ServerClient.GetUpdateManifestAsync + DownloadUpdateAsync;
//       GatewayUpdateManifest wire model.
//     • SAFETY: SHA256 verified before any install; loop-protection
//       (records attempted version — won't auto-retry the same version
//       if an install didn't take; a manual UPDATE_GATEWAY push
//       overrides); no-downgrade; never blocks HUD/commands/audit.
//   CHICKEN-AND-EGG: this build (N) must be installed BY HAND — it is
//   the first build that CARRIES the updater. Every build AFTER N can
//   be pushed automatically. So N is intended to be the last manual
//   Gateway install.
//   Also bumped MSI ProductVersion -> 1.0.4.0 (clean MajorUpgrade).
//   VERSION: GatewayBuild -> GW.0602.26-N.
//


//   FIX - the L audit shipper watched the WRONG directory. It used
//   %USERPROFILE%\Documents\NinjaTrader 8\BEV\AUDIT LOG, but the
//   Gateway runs as a LocalSystem service, so %USERPROFILE% resolves
//   to C:\Windows\system32\config\systemprofile — NOT the operator's
//   Documents. The shipper started fine but watched an empty system
//   folder and would never see EAGLE's real files. (Confirmed live:
//   "AuditShipper started. Watching C:\Windows\system32\config\
//   systemprofile\Documents\NinjaTrader 8\BEV\AUDIT LOG".)
//     • Now uses the SAME proven approach as WriteNexusDropAsync:
//       DiscoverAuditLogDirs() enumerates C:\Users\* for the real
//       <user>\Documents\NinjaTrader 8\BEV\AUDIT LOG dir(s), skipping
//       Public/Default/system profiles. Ships from every operator
//       audit dir found (normally just the one).
//     • Shipper scans all discovered dirs each cycle; startup log now
//       reads "Scanning C:\Users\*\Documents\NinjaTrader 8\BEV\AUDIT
//       LOG".
//   No behavior change to HUD/commands/health. Shipper logic only.
//   VERSION: GatewayBuild -> GW.0602.26-M.
//


//   ADD - Audit shipper. The Gateway now tails the EAGLE audit CSV
//   directory and POSTs each complete/rotated file to Hive
//   /v1/audit/ingest using its already-bound JWT + MID. This is the
//   production replacement for the manual ingest used to validate
//   the pipeline (item 11). Runs as a parallel loop off GatewayWorker
//   (like the command poll) — never blocks HUD cadence or commands.
//     • Source dir: %USERPROFILE%\Documents\NinjaTrader 8\BEV\AUDIT
//       LOG (recurses into dated subfolders). Scans every 30s.
//     • Rotation safety: a file must be unmodified for 20s before it
//       ships, so we never POST a CSV EAGLE is still writing. Opens
//       shared (FileShare.ReadWrite) so it never fights the writer.
//     • Local ship-state (audit-ship-state.json) records path+size+
//       mtime so a service restart doesn't re-POST everything; Hive
//       dedups by content hash regardless.
//     • Backpressure: <=25 files per scan cycle; if Hive transport
//       fails (endpoint down) the cycle stops and retries next scan
//       — nothing is marked shipped, no flooding.
//     • Failed-parse (Hive 500 on one bad file): logged, NOT marked
//       shipped, retried later; never blocks the rest of the batch.
//       (Pairs with Hive HV.0602.26-E which no longer leaves a
//       dedup-blocking ledger row on a failed parse.)
//     • Multi-box: each box ships under its own MID/JWT to a
//       stateless endpoint; no client-side serialization needed.
//     • New HiveClient.ShipAuditAsync; AuditIngestRequest/Response
//       wire models; AuditShipper.cs.
//   VERSION: GatewayBuild -> GW.0602.26-L.
//


//   FIX - J failed to compile (so no Service exe was emitted and the
//   MSI step then errored on a missing BEVGateway.Service.exe). Root
//   cause: StatusReporter.cs referenced "System.Globalization..."
//   with a bare System. prefix, but the Service has a local
//   "BEVGateway.Service.System" namespace (the System\ folder), so
//   the compiler resolved System. against THAT and failed
//   (CS0234: 'Globalization' does not exist in
//   'BEVGateway.Service.System'). Fixed by root-qualifying as
//   global::System.Globalization.DateTimeStyles.RoundtripKind.
//   No behavior change vs J — the silent-notifications + server-
//   anchored / Hive-grace-window health model from J is intact; this
//   is purely the compile fix.
//   VERSION: GatewayBuild -> GW.0602.26-K; MSI Version 1.0.2.0.
//


// -------------------------------------------------------------
//   FIX - root cause of the "connecting/disconnecting every 2-10
//   min" the trader was seeing was NOT the connection (service log
//   is unbroken 200s, Hive is healthy). It was TWO things the UI did:
//     (a) a Windows TOAST fired on every health RECOVERY — including
//         the harmless Yellow<->Green token-window / Hive-heartbeat
//         jitter — so a fine system still popped a notification every
//         few minutes. UNACCEPTABLE on a trading desktop.
//     (b) the icon itself went amber the instant the HiveUp flag
//         toggled, so a momentary Hive gap flashed the indicator even
//         with toasts suppressed.
//   CHANGES:
//     1. TRAY notifications are now SILENT on every healthy state and
//        every recovery. No toast for Green, Yellow, token-window
//        crossings, or Hive jitter. The icon color + status window
//        still update live every poll (the at-a-glance signal). The
//        ONLY toast that can fire is a genuine SUSTAINED outage (Red,
//        after the 4-miss ~20s debounce already in place since I).
//     2. SERVICE ComputeHealth re-anchored on the SERVER. Hive is
//        enrichment and is now judged by RECENCY of last good contact
//        (LastHudUtc within a 3-min grace window), not the instant
//        HiveUp flag. Server up + token healthy + Hive heard in the
//        last 3 min = GREEN, regardless of one or two missed pushes.
//        Hive only demotes to YELLOW (degraded, never Red on Hive
//        alone) after genuine multi-minute silence. This kills the
//        amber flash.
//   NET: as long as the Server connection holds and Hive is sending
//   heartbeats, the trader sees a steady GREEN with zero popups.
//   VERSION: GatewayBuild -> GW.0602.26-J; MSI Version 1.0.1.0.
//


// -------------------------------------------------------------
//   FIX - tray STILL flipped ONLINE <-> SERVICE UNREACHABLE on G
//   despite the F health fix. Confirmed via 13 min of clean 200s in
//   the service log: the SERVICE was rock-stable, so this was purely
//   the tray<->service NAMED-PIPE transport dropping on some polls,
//   not a health issue. The single-listener model (even re-armed
//   immediately) still raced when a connect landed between handoff
//   and re-arm. Three-layer hardening:
//     1. Service now runs a POOL of 4 concurrent pipe listeners, so
//        a tray connect always finds a free instance. Removes the
//        race at the source.
//     2. Tray miss-debounce raised 2 -> 4 consecutive misses (~20s
//        at the 5s poll) before showing UNREACHABLE, so a brief pipe
//        hiccup holds the last-good state; only a real service stop
//        (sustained silence) flips it.
//     3. Client connect timeout 1500->2000ms, retries 3->4 for more
//        headroom per poll.
//   ADD - build-stamped heartbeat log line ~once/minute
//   ("Heartbeat build=... mid=... hive=up server=up jwtMinLeft=...")
//   so any slice of the log identifies the build, not just startup.
//   VERSION: GatewayBuild -> GW.0602.26-I; MSI Version 1.0.0.0.
//

// DATE : 2026-06-02 15:20 EST
// -------------------------------------------------------------
//   TRAY ICON redesign per request: dropped the circle+"N" mark for
//   a NEXUS-header style box. Amber accent bar (#FFA940) on the left
//   edge, then a status-colored square with a heavy black outline:
//     Green box = both Server + Hive up
//     Amber box = half up / connecting (one side down, or near
//                 token expiry)
//     Red box   = both offline / unprovisioned
//   VERSION: GatewayBuild -> GW.0602.26-H; MSI Version 0.9.0.0.
//

// DATE : 2026-06-02 15:00 EST
// -------------------------------------------------------------
//   CHANGE - log filename date format YYYYMMDD -> MMDDYYYY
//   (e.g. gateway-20260602.log -> gateway-06022026.log). In-line
//   log timestamps remain ISO-8601 UTC (the wire/parse format) -
//   only the filename changed. Tray "Open Log Folder" opens the
//   directory so it is unaffected.
//   TRAY ICON - the health icon now fills the full icon area with
//   the status color (green/amber/red) and carries the brand "N"
//   overlay, so the status reads clearly at 16px instead of being a
//   tiny center dot. NOTE: whether the icon sits in the always-
//   visible tray vs the overflow (^) is a WINDOWS per-user setting,
//   not app-controllable - pin it once via Settings > Taskbar >
//   "Other system tray icons" > Nexus Gateway > On.
//   VERSION: GatewayBuild -> GW.0602.26-G; MSI Version 0.8.0.0.
//

// DATE : 2026-06-02 14:45 EST
// -------------------------------------------------------------
//   ROOT-CAUSE FIX for the persistent status blink (the real one;
//   D's pipe-gap fix + E's button fix were correct but did not
//   address THIS). Two independent loops were both writing the
//   HiveUp health flag on mismatched timers:
//     - HUD-push loop (15s request/response) set HiveUp true/false
//     - Command-poll loop (30s LONG-POLL) ALSO set HiveUp true/false
//   A long-poll cycling with no commands is normal, not a health
//   event, but it was flipping HiveUp false every ~30s; the HUD loop
//   set it true every 15s; the two fought and the tray (5s poll)
//   caught the flip -> blink on a ~30s cadence.
//   FIX:
//     1. The command loop NO LONGER writes HiveUp at all. A
//        long-poll cycle is not a health signal.
//     2. HiveUp is now owned SOLELY by the HUD push (the real 15s
//        heartbeat).
//     3. Hysteresis added: HiveUp only flips false after 2
//        CONSECUTIVE HUD failures, so a single blip/cold-start does
//        not blink the UI. Pairs with D's tray-side debounce.
//   VERSION: GatewayBuild -> GW.0602.26-F; MSI Version 0.7.0.0.
//

// DATE : 2026-06-02 14:20 EST
// -------------------------------------------------------------
//   FIX - DONE button on the status window hung below the bottom
//   edge. The button is a child of the inner `body` panel, but its
//   Location was computed in WINDOW coordinates (Height-50) instead
//   of panel coordinates; since the panel is offset down by the
//   titlebar height, the button landed ~titlebar px too low. Now
//   positioned relative to body.Height with Bottom|Right anchor, and
//   window height nudged 420->430 for clearance below LAST HUD.
//   VERSION: GatewayBuild -> GW.0602.26-E; MSI Version 0.6.0.0.
//

// DATE : 2026-06-02 14:05 EST
// -------------------------------------------------------------
//   FIX - tray flapped ONLINE <-> "SERVICE UNREACHABLE". This was
//   NOT a service crash (the morning crash-loop events in the log
//   were old; service ran clean post-install with steady hud 200s).
//   Root cause: a named-pipe LISTENER GAP. TrayIpcServer created
//   one pipe instance, accepted, handed off, THEN looped back to
//   create the next — leaving a brief window with no listener. A
//   tray poll landing in that window timed out and showed
//   "SERVICE UNREACHABLE", recovering next poll. Three-layer fix:
//     1. Service: transfer the accepted connection and loop back to
//        create+wait on the next instance immediately, so a listener
//        is always present (no gap).
//     2. Tray IpcClient: retry the connect up to 3x with a short
//        backoff before declaring failure — rides over any momentary
//        gap.
//     3. Tray TrayContext: debounce — a single missed poll holds the
//        last known-good state; only 2 consecutive misses flip the UI
//        to unreachable. Outage toast fires once on the Red
//        transition, not repeatedly.
//   VERSION: GatewayBuild -> GW.0602.26-D; MSI Version 0.5.0.0.
//

// DATE : 2026-06-02 12:40 EST
// -------------------------------------------------------------
//   BUILD FIX - -B failed to compile the MSI: WIX0103 "Cannot find
//   the Icon file 'NexusGateway.ico'". WiX resolves SourceFile
//   relative to its working directory, which build.ps1 sets to the
//   project root, not the installer\ folder. Changed the Icon
//   SourceFile from "NexusGateway.ico" to "installer\NexusGateway.ico".
//   GatewayBuild -> GW.0602.26-C; MSI Version 0.4.0.0. All -B
//   content (clean-build step, tray polish, branding) retained.
//

// DATE : 2026-06-02 12:25 EST
// -------------------------------------------------------------
//   BUILD-SYSTEM FIX (the reason -A appeared not to install):
//     • build.ps1 now force-cleans ALL obj/bin/dist plus the
//       generated harvest fragments BEFORE publishing. Previously
//       it only deleted the publish-output folders, so when the
//       source zip was re-expanded over an existing folder, the
//       stale compiled BEVGateway.Shared.dll (still stamped the
//       OLD build constant) sat in bin\Release and dotnet publish
//       relinked against it instead of recompiling. Net effect:
//       the installed service kept reporting GW.0528.26-A even
//       after a clean MSI uninstall/reinstall. Clean step makes a
//       fresh expand always compile fresh source.
//   VERSION
//     • GatewayBuild -> GW.0602.26-B; MSI Package Version 0.3.0.0
//       (bumped so MajorUpgrade reliably replaces files).
//   CONTENT otherwise identical to -A (tray notification policy,
//   UPPERCASE status, taller window, Open Log Folder from tray,
//   branded .ico + AUMID toast identity, idempotent provision +
//   MID-tolerant Hive already live server-side).
//

// DATE : 2026-06-02 11:30 EST
// -------------------------------------------------------------
//   TRAY POLISH + UX fixes (no service-logic changes):
//     1. Notification spam fixed. The "All systems normal." toast
//        was firing every poll (~10-30s) on any health transition,
//        including Yellow<->Green token-window jitter. Now toasts
//        ONLY on meaningful transitions: first Green after non-
//        Green, entering Red (outage), or recovering from Red.
//        Startup (Unknown->X) never toasts.
//     2. "Open Log Folder" now works. It was routed through the
//        service (LocalSystem, session 0) so Explorer opened
//        invisibly. The tray (interactive session) now opens
//        C:\ProgramData\NexusGateway\logs directly, creating it
//        if absent.
//     3. Status window taller (360->420) so the DONE button and
//        all rows fit without clipping.
//     4. All status values render UPPERCASE (TIER, TENANT, FLEET
//        ROLE, etc.); SERVER/HIVE dots show UP/DOWN.
//     5. Branded NEXUS icon (amber rounded square + "N") on the
//        status/setup window chrome. Toast app-name now reads
//        "Nexus Gateway" via AppUserModelID (was the raw process
//        name "BEVGateway.Tray").
//   INSTALLER (NexusGateway.wxs):
//     • Real multi-res NexusGateway.ico added (16-256px). Wired as
//       ARPPRODUCTICON (Add/Remove Programs icon), the Start Menu
//       shortcut icon, and the tray exe ApplicationIcon.
//     • Start Menu shortcut now carries System.AppUserModel.ID =
//       "BirdsEyeView.NexusGateway", matching the AUMID the tray
//       process sets. THIS is what makes Windows label toasts
//       "Nexus Gateway" with the brand logo instead of
//       "BEVGateway.Tray" + the generic blue (i) glyph. Requires a
//       rebuild + reinstall to take effect (the registered
//       shortcut is what Windows reads).
//     • ARPHELPLINK / ARPURLINFOABOUT -> bevcloud.app.
//   NOTE: exe name kept BEVGateway.Tray.exe (installer references
//   it); only friendly Product/Title metadata + AUMID changed.
//
// -------------------------------------------------------------
// BUILD: GW.0601.26-D  (Build_060126-Gateway-NX-D.zip)
// DATE : 2026-06-01 14:00 CST
// -------------------------------------------------------------
//   CRITICAL FIX - reprovision crash-loop that burned seats.
//   Symptom: DEV02 logs showed Hive returning 401 on hud-snapshot
//   and commands; the worker mapped that to AUTH_EXPIRED and
//   called ForceReprovisionAsync every cycle. Two overlapping
//   reprovisions raced on identity.json.tmp -> IOException ->
//   unhandled -> StopHost killed the service -> restart -> repeat,
//   each loop provisioning a NEW cube (seat-count climbed on its
//   own). Four hardening changes:
//     1. Hive 401 no longer triggers reprovision (in BOTH the HUD
//        push and command-poll loops). A Hive auth rejection means
//        Hive won't accept our token - NOT that our identity is
//        bad. Hive-down is non-fatal; trading never blocks on it.
//     2. IdentityStore.SaveAsync is now concurrency-safe: a
//        SemaphoreSlim lock + a unique per-write tmp filename
//        (no fixed .tmp to collide on).
//     3. ForceReprovisionAsync wrapped in try/catch - can never
//        throw out to the host.
//     4. HostOptions.BackgroundServiceExceptionBehavior = Ignore
//        so no future unhandled worker exception can tear down the
//        whole service.
//   NOTE: the Hive 401 ROOT CAUSE (Hive rejecting a valid Server
//   JWT) is a separate Hive-side auth issue, tracked separately.
//   This build stops the Gateway from self-harming over it.
//
//   Self-contained runtime (from -C) retained: bare-MSI installs
//   on any box, no .NET runtime needed. MSI ~56MB.
//
//
// ============================================================
//
// -------------------------------------------------------------
// BUILD: GW.0601.26-C  (Build_060126-Gateway-NX-C.zip)
// DATE : 2026-06-01 13:00 CST
// -------------------------------------------------------------
//   FIXED - "Service 'Nexus Gateway' failed to start" on a box
//   without the .NET 8 Desktop Runtime. Root cause: publish was
//   --self-contained false, so the service needed the runtime
//   pre-installed; running the bare MSI (skipping the install
//   script's runtime check) left the service unable to launch.
//
//   PERMANENT FIX - Build now publishes BOTH service and tray
//   --self-contained true (-r win-x64). The .NET 8 runtime is
//   bundled into the publish output and harvested into the MSI.
//   The target box needs NO separate runtime. Bare-MSI install
//   now works regardless of what's on the machine - the coupling
//   is done at the binary level, not via install order.
//   MSI grows by ~60-70MB (the bundled runtime) - expected.
//
//
// FILE        : CHANGE LOG.cs
// STATUS      : Phase 1c-2 — Nexus Gateway (NEXUS retrofit + bootstrapper)
// LAST UPD    : 2026-05-28 01:00 CST
// PURPOSE     : Build history for the Nexus Gateway component.
// OWNS        : Change log.
// CALLED BY   : N/A — reference only.
// ============================================================
//
