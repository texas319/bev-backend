// ============================================================
// FILE        : CHANGE LOG.cs
// STATUS      : Phase 1c-1 — Hive delta for Gateway lifecycle
// LAST UPD    : 2026-05-27 14:00 CST
// PURPOSE     : Master change log for BEV Hive backend.
// OWNS        : Build history.
// CALLED BY   : N/A — reference only.
// ============================================================
//
// ─────────────────────────────────────────────────────────────
// BUILD: HV.0611.26-A  (inbound frame ingress for NEXUS frames)
// DATE : 2026-06-11
// ─────────────────────────────────────────────────────────────
//   ADD — POST /v1/frame/publish (new file BEV Hive Frame Publish.cs).
//     Lets a NEXUS surface push a frame to the rail for frames that
//     originate in NEXUS in-process state the Hive can't read (option B,
//     no IPC). Auth = NEXUS dashboard JWT; scoped to the operator's
//     granted tenants (TENANT_OUT_OF_SCOPE, same rule as /v1/invoke);
//     relayed to t:{tenant} via SendToGroupAsync. Snapshot OR row-delta
//     (payload:null = tombstone, same as fleet.roster).
//   ALLOW-LIST — only NEXUS-authorable panel_ids accepted: replication.
//     config (Build 2), proposals.pending + seven.thread (Build 3).
//     Hive-owned ids (fleet.roster/aggregate, header.assimilated) are
//     NOT authorable from outside — cannot be spoofed in.
//   UNBLOCKS — Build 2 replication.config: NEXUS is the producer (role/
//     copy/risk are all in-process NEXUS state); this is the ingress it
//     posts to. Same path serves Build 3 Seven frames.
//   VERSION: HIVE_BUILD_LABEL -> HV.0611.26-A.
//
// ─────────────────────────────────────────────────────────────
// BUILD: HV.0610.26-A  (fleet.aggregate + catalog v1.1)
// DATE : 2026-06-10
// ─────────────────────────────────────────────────────────────
//   ADD — fleet.aggregate frame (Build 1, cross-cube source of truth).
//     New file BEV Hive Fleet Aggregate.cs: FleetAggregator.Build rolls
//     per-instance fleet_live snapshots into per-account -> per-cube
//     subtotal -> fleet total. LIVE-only headline (trace_mode=false),
//     SIM as a separate split line. Pushed on /v1/fleet/live ingest to
//     the tenant group, alongside fleet.roster + header.assimilated.
//     Store: GetFleetLiveRawAsync (raw mid+snapshot rows). Best-effort;
//     never blocks ingest. (Multi-tenant scoping needs a mid->tenant
//     column; un-scoped today, matching GetFleetRosterAsync.)
//   CATALOG — function-catalog.json -> v1.1, 42 -> 48 functions:
//     +control.amc.set_copy (write), +control.seven.proposal_accept/
//     proposal_reject (write), +control.kill.engage_cube /
//     control.positions.flatten_cube / control.session.halt_cube
//     (nuclear, mid arg). Per-cube + seven + set_copy route through the
//     existing RouteCommand path (mid arg already scopes the command;
//     write/nuclear already fan as commands) — no new invoke code.
//   VERSION: HIVE_BUILD_LABEL -> HV.0610.26-A.
//
// ─────────────────────────────────────────────────────────────
// BUILD: HV.0607.26-L  (operator tenant-grant claims)
// DATE : 2026-06-07
// ─────────────────────────────────────────────────────────────
//   CHANGE — dashboard JWT now carries a TENANTS list (was fleet_ids).
//     Validator reads claim "tenants"; negotiate joins one SignalR group
//     per granted tenant (t:{tenant}); /v1/invoke scopes a tenant_id arg
//     against the operator grants (TENANT_OUT_OF_SCOPE).
//   VERSION: HIVE_BUILD_LABEL -> HV.0607.26-L.
//

// ─────────────────────────────────────────────────────────────
// BUILD: HV.0606.26-K  (Build_060626-Hive-K.zip)
// DATE : 2026-06-06
// PHASE: 2 / Drop 2 — CONTROL PLANE (the WRITE plane)
// ─────────────────────────────────────────────────────────────
//   ADD — POST /v1/invoke. Single write/control entry point.
//     Validates dashboard JWT, looks up function_id in the embedded
//     BEV Function Catalog (server-side allow-list; unknown = 404),
//     enforces tier (read/write/nuclear), scopes account/fleet args
//     against the JWT fleet_ids (out-of-scope = 403), stamps the actor
//     email server-side (non-falsifiable), and routes the action as an
//     INVOKE command for the target Cube Gateway to drain.
//   ADD — Nuclear two-phase: confirm==true with no token -> status=
//     pending + 30s confirm_token + control.nuclear_pending frame;
//     repeat with the token -> executes + control.nuclear_engaged
//     announcement frame to the tenant group.
//   ADD — FunctionCatalog loader (function-catalog.json shipped with
//     the build) + IHiveStorage.EnqueueCommandAsync (INVOKE command
//     into the cycles container, drained by GET /v1/commands).
//   NOTE — 42-function contract: 9 read / 29 write / 4 nuclear, matching
//     NEXUS BEV Function Catalog.json catalog_version.
//   VERSION: HIVE_BUILD_LABEL -> HV.0606.26-K.
//

// ─────────────────────────────────────────────────────────────
// BUILD: HV.0602.26-J  (Build_060526-Hive-J.zip)
// DATE : 2026-06-05
// PHASE: 2 / Drop 1 — LIVE RAILS (Azure SignalR Serverless)
// ─────────────────────────────────────────────────────────────
//   ADD — POST /v1/realtime/negotiate. Validates a dashboard JWT
//     (role=dashboard), mints a SignalR client token (userId=email),
//     joins the user to tenant + per-fleet SignalR groups, returns
//     { url, accessToken }. Web/desktop NEXUS connects with this.
//   ADD — SignalRService (REST): client-token gen, group membership,
//     scoped push (group/user/broadcast), all signed with the SignalR
//     AccessKey. Hub "nexus", target "frame". No binding extension.
//   ADD — live push on the fleet/live ingest path: fleet.roster as a
//     per-row delta (row_key = mid:instance), header.assimilated as a
//     snapshot, both to the tenant group.
//   ADD — GET /v1/tenant/pnl for fleet.pnl_total (sum net_pnl for the
//     latest Eastern session_date; accounts = distinct account_tier).
//   ADD — JWT validator now also reads dashboard claims (role, sub,
//     fleet_ids) alongside the existing cube claims (additive).
//   REQUIRES app setting: AzureSignalRConnectionString.
//   VERSION: HIVE_BUILD_LABEL -> HV.0602.26-J.
//

// ─────────────────────────────────────────────────────────────
// BUILD: HV.0602.26-I  (Build_060426-Hive-I.zip)
// DATE : 2026-06-04 13:00 EST
// PHASE: 1e — FleetView refinements
// ─────────────────────────────────────────────────────────────
//   ADD — GET /v1/assimilated. Returns the global trades-assimilated
//     count (every TCA row, fleet-wide). This is the authoritative
//     PHX/DRG number — same source they reason against. Bearer cube
//     auth. Gateway pulls it per cycle and surfaces it on every tray.
//   FIX — US Eastern on the fleet roster. GetFleetRosterAsync now
//     returns timestamps converted to America/New_York via AT TIME
//     ZONE (last_live_et, last_ship_et) so NEXUS FleetView renders
//     Eastern natively. Platform rule: only US Eastern, everywhere.
//   VERSION: HIVE_BUILD_LABEL -> HV.0602.26-I.
//

// ─────────────────────────────────────────────────────────────
// BUILD: HV.0602.26-H  (Build_060426-Hive-H.zip)
// DATE : 2026-06-04 00:35 EST
// PHASE: 1e — FleetView backend
// ─────────────────────────────────────────────────────────────
//   ADD — FleetView backend (NEXUS consolidated fleet panel).
//     • POST /v1/fleet/live — the Gateway forwards each EAGLE
//       BevLiveSnapshot here; upserted into audit.fleet_live keyed
//       by (mid, instance_id), latest-wins. Bearer cube auth; MID
//       normalized to canonical C-.
//     • GET /v1/fleet — NEXUS reads the consolidated roster: every
//       box (C- MID) with its live instances (state/position/pnl/
//       trace_mode/families/regime) MERGED with an audit roll-up
//       (tca count + last session + files shipped) from Postgres.
//       FULL OUTER JOIN so live-only or audit-only boxes still show.
//     • New table audit.fleet_live (003_fleet_live.sql).
//     • Store: UpsertFleetLiveAsync + GetFleetRosterAsync.
//   FIX — NormalizeMid returns NULL (not bare "C-") for empty input,
//     so blank legacy MIDs no longer create a phantom "C-" roster row.
//   VERSION: HIVE_BUILD_LABEL -> HV.0602.26-H.
//

// ─────────────────────────────────────────────────────────────
// BUILD: HV.0602.26-G  (Build_060326-Hive-G.zip)
// DATE : 2026-06-03 22:30 EST
// PHASE: 2 — AUDIT PIPELINE — canonical C- MID
// ─────────────────────────────────────────────────────────────
//   FIX — the ledger `mid` column was coming back BLANK, so you
//   could not tell which Cube shipped which file ("which VPS are
//   connected" returned an empty MID). Two causes, both fixed:
//     (1) ingest recorded the ledger MID from claims.MachineId,
//         but the JWT did not carry that claim populated — now the
//         MID resolves JWT claim -> X-MID header (the Gateway
//         always sends this) -> MID parsed from the file name.
//     (2) no canonical format — some MIDs were bare hex ("55A857")
//         and some were "C-" prefixed. Platform rule is now ONE
//         canonical form: "C-XXXXXX". NormalizeMid() strips any
//         existing prefix and re-applies exactly one "C-", applied
//         to BOTH the ledger MID and the data-table mid (parsed
//         from the file name). Uniform everywhere on the way in.
//   BACKFILL — 002_mid_canonical_backfill.sql rewrites all existing
//   rows (ledger + every data table incl. partitioned barsnap) to
//   the canonical "C-XXXXXX" form. Idempotent.
//   AFTER THIS: SELECT mid, count(*) FROM audit.ingest_ledger
//   GROUP BY mid  becomes a real per-Cube fleet roster.
//   No schema change. Parser + ingest logic only.
//   VERSION: HIVE_BUILD_LABEL -> HV.0602.26-G.
//

// ─────────────────────────────────────────────────────────────
// BUILD: HV.0602.26-F  (Build_060326-Hive-F.zip)
// DATE : 2026-06-03 16:55 EST
// PHASE: 2 — AUDIT PIPELINE — US Eastern session_date (platform rule)
// ─────────────────────────────────────────────────────────────
//   FIX — session_date was computed with a hardcoded AddHours(-4),
//   i.e. EDT only. That is WRONG for Nov-Mar when US Eastern is EST
//   (-5): an event just after UTC midnight in winter would be filed
//   under the wrong calendar day, mis-dating rows and mis-routing
//   barsnap partitions. Platform rule is now ONLY US Eastern for all
//   session accounting.
//     • SessionDate now converts the explicit UTC timestamp to
//       America/New_York via TimeZoneInfo (DST-correct year-round:
//       EST -5 in winter, EDT -4 in summer, automatically). Resolves
//       the IANA id "America/New_York" first, falls back to Windows
//       "Eastern Standard Time", then a fixed-EST custom zone as last
//       resort.
//     • Applies to every table's session_date (tca/sigeval/barsnap/
//       order_lifecycle/trace/diag/spec).
//   NOTE (NOT changed in F, pending confirmation): the bare TRACE/DIAG
//   timestamp ("MM-dd-yy HH:mm:ss", no TZ marker) is still parsed as
//   UTC (AssumeUniversal). If EAGLE actually writes that field in
//   Eastern wall-clock, it needs to be interpreted as Eastern and
//   converted to UTC — but that is a separate field and a 4-5h skew
//   if guessed wrong, so it is left untouched until confirmed against
//   live data (compare a trace row's time to the same trade's TCA
//   timestamp_utc). Flagged to revisit.
//   No schema change. Parser logic only.
//   VERSION: HIVE_BUILD_LABEL -> HV.0602.26-F.
//


//   FIX — a FAILED parse used to record its content SHA in the
//   ingest_ledger, which made the dedup guard treat that exact
//   content as "already seen" and BLOCK a corrected build from
//   re-ingesting it. (Surfaced live: a TCA file POSTed during the
//   pre-fix DateTimeStyles crash got ledger-stamped FAILED and was
//   then skipped as a duplicate, leaving tca at 19 instead of 21
//   until the stale ledger row was cleared by hand.)
//     • On parse failure, RecordLedgerAsync is now called with a
//       NULL content_sha256 (logged for visibility, but NULLs are
//       distinct in the unique index so they never dedup-block).
//       A later successful re-POST records the real sha normally.
//     • RecordLedgerAsync sha param is now nullable (string?),
//       DBNull-safe.
//   This is the hardening Risk flagged for the Gateway shipper:
//   transient/parse failures no longer poison future retries.
//   No schema change. No endpoint surface change.
//   VERSION: HIVE_BUILD_LABEL -> HV.0602.26-E.
//


//   FIX — full live ingest surfaced 3 files (1 each of diag, trace,
//   sigeval) returning HTTP 500. Root cause: rotated/concatenated
//   session files can START with a data row (real header appears on
//   a later line) and can repeat the header mid-file. The parser
//   assumed row 0 was always the header, so it parsed literal column
//   names as data (sigeval crash) or dropped whole files (diag/trace
//   yielded 0 rows).
//     • AuditRouter now FINDS the header row by content (FindHeaderRow
//       / IsHeaderRow: first cell == "timestamp_utc" for wide types,
//       "Timestamp" for trace/diag) instead of assuming records[0].
//     • BuildWide + BuildTrace now skip ANY row matching the header
//       signature (the header itself + every mid-file repeat) and
//       iterate from row 0 so a leading data row is no longer lost.
//     • BOM-tolerant first-cell compare.
//   Re-validated against full bundle (163 files, 0 failures); the 3
//   previously-failing files now yield 25 / 85 / 20 rows. No schema
//   change, no endpoint change. Parser only.
//   VERSION: HIVE_BUILD_LABEL -> HV.0602.26-D.
//


//   FIX — first live ingest surfaced a parser crash (PARSE_FAILED,
//   HTTP 500). AuditRouter.ParseIso combined DateTimeStyles
//   RoundtripKind | AdjustToUniversal, which .NET rejects at
//   runtime ("RoundtripKind cannot be used with AssumeLocal,
//   AssumeUniversal or AdjustToUniversal"). RoundtripKind already
//   honors the Z/offset in the ISO string, so AdjustToUniversal
//   was both illegal and redundant. Now: parse with RoundtripKind
//   alone, then normalize to UTC (SpecifyKind Utc if Unspecified,
//   else ToUniversalTime). ParseTraceTs (AssumeUniversal |
//   AdjustToUniversal) is a LEGAL combo and unchanged.
//   No schema change. No endpoint change. Parser logic only.
//   VERSION: HIVE_BUILD_LABEL -> HV.0602.26-C.
//


//   ADD — the audit/AI organ. Hive stops being a relay-only
//   mailbox and gains durable storage that the AI reasons over.
//     • 8-table Postgres schema (schema `audit`): tca (131-col),
//       order_lifecycle (OCO-indexed), sigeval (gate_details JSONB),
//       barsnap (partitioned by session_date + per-cube perf cols),
//       trace + diag (PascalCase->snake, raw_payload JSONB, diag
//       isolated per Risk), spec, perf/settings (EAV), ingest_ledger.
//       File: sql/001_audit_schema.sql (idempotent).
//     • POST /v1/audit/ingest — classifies any of 8 log types,
//       parses + type-coerces, writes to the matching table.
//       Bearer JWT auth (tenant/MID from token sub, same authoritative
//       model as hud-snapshot). Idempotent via content SHA in ledger
//       (re-POST = DUPLICATE, no double insert). ADDITIVE — the relay
//       endpoints (hud-snapshot/commands/etc) are untouched.
//     • AuditRouter (C#) is a direct port of a reference parser
//       VALIDATED against the full live bundle BEV-EAGLE-06-02-26:
//       163 CSV files, 0 failures. Handles every locked quirk —
//       orphan trades (77, no TCA), NULL exit_name, bare-or-granular
//       close_reason, multi-build same-day, legacy v1-26.* + new
//       BEV.0602.26-AA build strings, trace legacy timestamps +
//       repeated-header/blank-row skip, JSONB passthrough.
//     • PostgresAuditStore: data-driven INSERTs (column set from
//       information_schema, cached), barsnap daily partitions created
//       on demand, JSONB params, AUDIT_PG_CONN from app settings.
//     • L2 enrichment columns (5 + feed_source) pre-added nullable on
//       tca/sigeval, ready for item 15.
//   VALIDATION: INGEST_VALIDATION_REPORT.txt (row counts + sample
//   analytical queries). Memo to Risk: MEMO_TO_RISK_INGEST_RESULTS.txt.
//   DEPLOY: AUDIT_DEPLOY_RUNBOOK.txt (apply migration, set AUDIT_PG_CONN,
//   publish, smoke test). Needs a Postgres instance + AUDIT_PG_CONN.
//   VERSION: HIVE_BUILD_LABEL -> HV.0602.26-B.
//


//   FIX
//     • hud-snapshot no longer 401s on MID mismatch. The JWT is
//       signed + validated, so the token's `sub` (claims.MachineId)
//       is the authoritative machine id. The X-MID header and
//       body.Mid are now advisory: when they differ from the token
//       sub (which happens for one cycle right after a Server
//       reprovision mints a fresh MID), we LOG the divergence and
//       persist under the token sub instead of rejecting.
//     • Root symptom: tray flapped green/yellow because commands
//       (Bearer-only) returned 200 while hud-snapshot returned 401
//       MID_MISMATCH on the same token. Removed MID_MISMATCH and
//       MID_BODY_MISMATCH rejections; MISSING_MID_HEADER downgraded
//       (header now optional).
//   UNCHANGED
//     • commands, command-ack, seven/query auth paths.
//     • All other validation (Bearer presence, JWT validity).
//
// ─────────────────────────────────────────────────────────────
// BUILD: v0-26.0527-A  (Build_052726-Hive-A.zip)
// DATE : 2026-05-27 14:00 CST
// PHASE: 1c-1 — Hive delta for Gateway lifecycle
// ─────────────────────────────────────────────────────────────
//   NEW
//     • Cosmos package added to Hive csproj. Program.cs now
//       registers CosmosClient + IHiveStorage in DI.
//     • Models: HUD payload DTOs (HudSnapshotRequest with strategy,
//       AMC, feeds, audit_tail blocks per Gateway memo §1.1),
//       command DTOs (GatewayCommand, CommandsResponse,
//       CommandAckRequest), CommandKinds enum (PING, RESTART_NT8,
//       REBOOT_BOX, KILL_ALL, REFRESH_CREDENTIALS), Cosmos doc
//       shapes (HudSnapshotDoc TTL=24h on `telemetry`,
//       CommandDoc TTL=7d on `cycles`).
//     • HiveStorage: UpsertHudSnapshotAsync, FindPendingCommands
//       Async (cross-Cube within tenant partition),
//       MarkDeliveredAsync (per-command idempotent stamp),
//       AckCommandAsync.
//     • host.json functionTimeout bumped 30s → 45s to support
//       the 30s long-poll budget on /v1/commands.
//
//   ENDPOINTS
//     • POST /v1/hud-snapshot (anon HTTP, JWT auth at app layer)
//       — Validates Bearer + X-MID header matches token claim,
//         persists snapshot to Cosmos telemetry container with
//         24h TTL, returns next_poll_sec=15 + rulebook_version
//         stub.
//     • GET  /v1/commands (anon HTTP, JWT auth at app layer)
//       — Long-poll up to 30s. Reads pending commands from
//         Cosmos cycles container filtered by tenant + machineId
//         + not-yet-acked + not-yet-expired + since_utc cursor.
//         Marks delivered when returned. Empty array if nothing
//         lands in the budget window.
//     • POST /v1/command-ack (anon HTTP, JWT auth at app layer)
//       — Records command outcome (success/failed/skipped/
//         expired) + detail + executed_utc. Unknown command_id
//         returns 200 with log warning (Gateway shouldn't loop).
//
//   ARCHITECTURAL NOTES
//     • HUD snapshot Cosmos doc id = `{mid}-{yyyyMMddHHmm}`. One
//       row per Cube per minute. Rapid Gateway updates within
//       the same minute overwrite. Storage churn stays bounded.
//     • Cycles container holds command lifecycle. Commands are
//       issued by external surface (admin endpoint deferred —
//       drop rows directly to test). Delivered timestamp set
//       on first poll pickup; acked timestamp set on ack.
//     • Long-poll budget 30s within 45s function timeout leaves
//       comfortable headroom for cosmos query latency.
//     • Real rulebook emission lands Phase 3; stub returns
//       "rb-stub-1" so Gateway can wire the comparison logic now.
//
//   DEFERRED TO LATER PHASES
//     • POST /v1/admin/command (issue a command)        — Phase 1c-2
//     • GET  /v1/credentials/gemini                     — Phase 1d
//     • /v1/l2/snapshot + /v1/l2/stream                 — Phase 1d+
//     • Postgres audit ingest                           — Phase 2
//     • Real rulebook emission                          — Phase 3
//
// ─────────────────────────────────────────────────────────────
// BUILD: v0-26.0524-B  (Build_052426-Hive-A.zip)  [DEPLOYED]
// DATE : 2026-05-24 13:00 CST
// PHASE: 1b — Hive /v1/seven/query stub
// ─────────────────────────────────────────────────────────────
//   NEW
//     • Project skeleton: .NET 8 isolated worker, Functions v4.
//     • DI wiring (Key Vault via DefaultAzureCredential / managed
//       identity). No Cosmos in 1b — query is a stub.
//     • Models: Mode enum (Eagle/Phoenix/Dragon), SevenQueryRequest,
//       SevenQueryResponse, PromptBlock, ClientBlock, UsageBlock,
//       StructuredBlock, ErrorResponse. Field names match Hive
//       Briefing Memo Rev 2 Section 2 exactly.
//     • JwtValidator: local validation against shared signing key
//       in Key Vault (server-jwt-signing-key, same key Server mints
//       with). Hour-cached. No remote call to Server per request.
//
//   ENDPOINTS
//     • POST /v1/seven/query (anon HTTP, JWT auth at app layer)
//     • GET  /v1/health (anon)
//
// ============================================================
