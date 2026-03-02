# CS2-Integrity-Gate

`CS2-Integrity-Gate` is a standalone anti-cheat gating stack for CS2 custom infrastructure.

It enforces this flow:

1. Player launches through your launcher.
2. Client anti-cheat service attests health to backend.
3. Backend issues a short-lived, single-use join token.
4. Server-side bridge validates token and heartbeat status before allowing join.
5. Server telemetry is ingested for rules and behavioral detections.
6. Enforcement actions are exposed from backend (kick/cooldown/ban pipeline foundation).

This repository is a runnable MVP foundation designed for integration into your own queue, backend, and CS2 server plugin stack.

## Table of Contents

1. [Repository Overview](#repository-overview)
2. [Architecture](#architecture)
3. [Core Flows](#core-flows)
4. [Components](#components)
5. [API Surface](#api-surface)
6. [Telemetry and Detection](#telemetry-and-detection)
7. [Persistence Model](#persistence-model)
8. [Configuration](#configuration)
9. [Setup and Run](#setup-and-run)
10. [Runbook](#runbook)
11. [Integration Notes](#integration-notes)
12. [Security Notes](#security-notes)
13. [Troubleshooting](#troubleshooting)
14. [Project Layout](#project-layout)
15. [Current Scope and Roadmap](#current-scope-and-roadmap)

## Repository Overview

This repo contains six active .NET projects plus staged domain folders and scripts:

- `src/ControlPlane.Api`: backend control plane API.
- `src/AcClient.Service`: local anti-cheat service process.
- `src/Launcher.App`: launcher orchestration CLI/app baseline.
- `tools/simulators/ServerBridge.Agent`: server integration simulator/bridge.
- `src/Shared.Contracts`: strongly typed shared contracts.
- `src/Cs2.Plugin.CounterStrikeSharp`: plugin adapter runtime scaffold (join gate, telemetry, health/action polling, ack flow).
- `src/Cs2.Plugin.Metamod`: reserved for alternate plugin integration wrapper.
- `src/Kernel.Driver`: reserved for kernel component.
- `src/Kernel.Bridge`: reserved for user/kernel communication layer.
- `src/Reviewer.Console`: moderation and appeals CLI for internal endpoints.
- `analytics/detection-tuning`: reserved for detection calibration jobs.
- `ops/`: infrastructure, observability, security, and runbooks.
- `scripts/`: helper scripts for startup and smoke tests.

The full design spec is included in:

- `ANTICHEAT_FULL_BUILD_SPEC.md`

## Architecture

High-level architecture:

1. Launcher (player machine)
- Authenticates user.
- Requests queue placement.
- Writes active match session state for AC service.
- Waits for AC-issued join token file.
- Passes token to CS2 launch command.

2. AC Client Service (player machine)
- Maintains device keypair.
- Enrolls device with control plane.
- Starts match attestation for current session.
- Sends periodic heartbeats.
- Writes fresh join token to shared runtime path.

3. Control Plane API (backend)
- Auth + queue endpoint set.
- Attestation and heartbeat validation.
- Join token issuance and replay protection.
- Match health state for server checks.
- Telemetry ingestion endpoints.
- Detection score and enforcement action output.
- SQLite persistence for all key entities.

4. Server Bridge (server side)
- Validates join token with backend.
- Streams tick/shot/LoS telemetry.
- Polls health/actions.
- Applies action recommendations in integration layer.

## Core Flows

## 1) Enrollment and Device Binding

1. AC service generates ECDSA private key if missing.
2. AC posts `/v1/attestation/enroll` with account, steam, public key.
3. Backend returns:
- `device_id`
- `device_certificate`
- `policy_version`
- `requirements_tier`

## 2) Queue and Match Session

1. Launcher posts `/v1/queue/enqueue`.
2. Backend returns:
- `match_session_id`
- `server_id`
- queue metadata.
3. Launcher writes `runtime/session.json`.

## 3) Match Start Attestation

1. AC reads `runtime/session.json`.
2. AC posts `/v1/attestation/match-start` with:
- platform posture signals.
- integrity signals.
- nonce/device/account/match metadata.
3. Backend validates:
- player belongs to match session.
- Tier A posture satisfied.
- basic integrity checks pass.
4. Backend returns join token (short TTL).
5. AC writes `runtime/join-token.json`.

## 4) Join Validation Gate

1. Launcher includes token in launch command.
2. Server bridge/plugin validates via `/v1/attestation/validate-join`.
3. Backend enforces:
- valid signature.
- token claims match request.
- token exists and not used.
- heartbeat exists and is fresh/healthy.
4. If valid:
- token marked used.
- player allowed.
5. If invalid:
- player denied.

## 5) Continuous Heartbeat

1. AC posts `/v1/attestation/heartbeat` at configured interval.
2. Backend updates:
- heartbeat logs.
- player health state.
3. Server queries `/v1/attestation/match-health`.
4. If stale/unhealthy:
- bridge/plugin can kick or move to spectator.

## 6) Telemetry and Enforcement

1. Server posts telemetry:
- ticks -> `/v1/telemetry/ticks`
- shots -> `/v1/telemetry/shots`
- LoS samples -> `/v1/telemetry/los`
2. Detection engine updates suspicion channels.
3. Backend stores scores and exposes actions:
- `/v1/enforcement/actions/{matchSessionId}`

## Components

## `ControlPlane.Api`

Main capabilities:

- Endpoint hosting and orchestration: `src/ControlPlane.Api/Program.cs`
- Policy/config types: `src/ControlPlane.Api/Options/`
- Token signing/validation: `src/ControlPlane.Api/Security/JoinTokenService.cs`
- Detection pipeline: `src/ControlPlane.Api/Services/DetectionEngine.cs`
- Storage and schema: `src/ControlPlane.Api/Persistence/SqliteStore.cs`

Notable behaviors:

- Join tokens are single-use.
- Replay attempt returns `token_replayed`.
- Heartbeat freshness is enforced using configured grace window.
- Policy is config-driven for token TTL and heartbeat cadence.

## `AcClient.Service`

Main capabilities:

- Long-running worker loop: `src/AcClient.Service/Worker.cs`
- Device key generation and persistence.
- Enrollment and match-start calls.
- Heartbeat scheduling.
- Shared runtime file output:
  - `runtime/session.json` (input from launcher)
  - `runtime/join-token.json` (output for launcher/game)

## `Launcher.App`

Main capabilities:

- Queue orchestration and runtime session write.
- Waits for a valid join token generated by AC.
- Prints CS2 launch command with join token.
- Optional `--self-validate` exists for diagnostics only.

## `tools/simulators/ServerBridge.Agent`

Main capabilities:

- Simulates server-side gate behavior through `Cs2.Plugin.CounterStrikeSharp` runtime.
- Uses host-bridge callbacks for allow/deny/action handling.
- Sends synthetic telemetry via runtime buffering and flush path.
- Polls pending actions and sends action acknowledgments.

Use this as the runtime integration reference before binding to a real CS2 plugin host.
## `Cs2.Plugin.CounterStrikeSharp`

Main capabilities:

- Runtime orchestrator for real plugin binding:
  - join validation on connect attempt,
  - telemetry buffering and flush,
  - match-health polling and transition-based enforcement,
  - pending-action polling and acknowledgment.
- Typed backend client with server API-key auth header.
- Host bridge abstraction (`IPluginHostBridge`) so game-framework glue remains isolated.
- CounterStrikeSharp adapter skeleton ready for event-hook wiring.

## `Shared.Contracts`

Central shared DTO contract library used by all services:

- Auth/queue DTOs.
- Attestation and heartbeat DTOs.
- Join validation DTOs.
- Telemetry DTOs (`TickPlayerState`, `ShotEvent`, `LosSample`).
- Detection score and enforcement action DTOs.

## `Reviewer.Console`

Main capabilities:

- Internal moderation CLI with `X-Internal-Api-Key` auth.
- Evidence listing and lookup.
- Review-case creation and status updates.
- Ban creation and appeal lifecycle handling.

## API Surface

From `src/ControlPlane.Api/Program.cs`.

System:

- `GET /`
- `GET /healthz`
- `GET /v1/policy/current`

Auth + Queue:

- `POST /v1/auth/login`
- `POST /v1/queue/enqueue`

Attestation:

- `POST /v1/attestation/enroll`
- `POST /v1/attestation/match-start`
- `POST /v1/attestation/heartbeat`
- `POST /v1/attestation/validate-join`
- `GET /v1/attestation/match-health?matchSessionId=...`

Queue-tier enforcement behavior:

- `standard` queue: Tier A posture required (`SecureBoot`, `TPM 2.0`).
- `high_trust` queue: Tier A + configured Tier B requirements (currently IOMMU and optional VBS depending on policy).

Telemetry:

- `POST /v1/telemetry/ticks`
- `POST /v1/telemetry/shots`
- `POST /v1/telemetry/los`

Detection/Enforcement read APIs:

- `GET /v1/detections/scores/{matchSessionId}/{accountId}`
- `GET /v1/enforcement/actions/{matchSessionId}`
- `GET /v1/enforcement/actions/{matchSessionId}/pending?accountId=...`
- `POST /v1/enforcement/actions/ack`

Evidence/Moderation APIs:

- `GET /v1/evidence?matchSessionId=...&accountId=...`
- `GET /v1/evidence/{evidenceId}`
- `POST /v1/review/cases`
- `GET /v1/review/cases?status=...`
- `POST /v1/review/cases/update`
- `POST /v1/moderation/bans`
- `GET /v1/moderation/bans/{banId}`
- `POST /v1/moderation/appeals`
- `GET /v1/moderation/appeals?status=...`
- `POST /v1/moderation/appeals/resolve`


Server-auth protected endpoints:

- Require header `X-Server-Api-Key`:
  - `/v1/attestation/validate-join`
  - `/v1/attestation/match-health`
  - `/v1/telemetry/*`
  - `/v1/enforcement/actions/*`

Internal-auth protected endpoints:

- Require header `X-Internal-Api-Key`:
  - `/v1/evidence/*`
  - `/v1/review/*`
  - `/v1/moderation/*`

## Telemetry and Detection

Implemented channels in detection engine:

- `rules`: impossible-state style checks (fire cadence violations, movement envelopes).
- `aim`: high sustained hit ratio signal.
- `trigger`: near-zero on-target fire latency proxy.
- `info`: LoS-inconsistent advantage proxy.
- `movement`: extreme speed consistency checks.

Current implementation stance:

- Conservative, sample-count aware scoring.
- Emits score updates and action recommendations.
- Immediate action currently modeled as kick for severe rule patterns.

Important:

- This MVP detection logic is baseline scaffolding.
- Production deployment requires calibration against large real-match baselines and strict review workflows.

## Persistence Model

SQLite database path (default):

- `data/controlplane.db`
- Evidence JSON output (default when running API project): `src/ControlPlane.Api/evidence/`

Tables created automatically by `SqliteStore.InitializeAsync`:

- `accounts`
- `steam_links`
- `devices`
- `match_sessions`
- `match_players`
- `join_tokens`
- `heartbeats`
- `player_health`
- `telemetry_events`
- `suspicion_scores`
- `enforcement_actions`
- `enforcement_action_acks`
- `evidence_packs`
- `review_cases`
- `review_case_events`
- `bans`
- `appeals`

Data captured:

- identity bindings.
- session allocation.
- token issuance/use.
- heartbeat history.
- telemetry event stream.
- per-channel suspicion scores.
- enforcement action records.
- evidence pack metadata and hashes.
- review/moderation case history.

## Configuration

## Control Plane

File: `src/ControlPlane.Api/appsettings.json`

Key sections:

- `Storage.SqlitePath`
- `AcPolicy.JoinTokenSecret`
- `AcPolicy.JoinTokenTtlSec`
- `AcPolicy.HeartbeatIntervalSec`
- `AcPolicy.GraceWindowSec`
- `AcPolicy.DefaultQueueTier`
- `AcPolicy.PolicyVersion`
- `AcPolicy.RequiredTierA`
- `AcPolicy.RequiredTierB`
- `ApiAuth.ServerApiKey`
- `ApiAuth.InternalApiKey`
- `Evidence.StorageDirectory`
- `Evidence.RecentTelemetryLimit`
- `Evidence.RecentHeartbeatsLimit`

Production notes:

- Replace `JoinTokenSecret` with a long random secret before any real deployment.
- Move secrets to environment variables or secret manager.

Key environment variables used by tooling:

- `CS2IG_SERVER_API_KEY` (launcher self-validate and simulator default)
- `CS2IG_INTERNAL_API_KEY` (Reviewer.Console default)
- `ApiAuth__ServerApiKey` and `ApiAuth__InternalApiKey` (control-plane overrides)

## AC Client

File: `src/AcClient.Service/appsettings.json`

Key section: `AcClient`

- `BackendBaseUrl`
- `AccountId`
- `SteamId`
- `LauncherVersion`
- `AcVersion`
- `SessionFilePath`
- `JoinTokenOutputPath`
- `DeviceKeyPath`
- `PolicyHash`

Paths are configured to align with repository-level `runtime/` by default.

## Setup and Run

Prerequisites:

- Windows with PowerShell.
- .NET SDK 10.x.

Build:

```powershell
dotnet build Cs2AcStack.slnx
```

## Fastest local test

```powershell
powershell -ExecutionPolicy Bypass -File scripts/smoke-test.ps1
```

Expected result:

- Launcher acquires join token.
- Server bridge accepts join.
- Simulated cheat telemetry triggers rule-based actions.

Reviewer end-to-end demo:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/reviewer-demo.ps1
```

This executes launcher + AC + server simulation and then runs evidence/review/ban/appeal operations through `Reviewer.Console`.

## Manual run (4 terminals)

Terminal 1:

```powershell
dotnet run --project src/ControlPlane.Api --urls http://localhost:5042
```

Terminal 2:

```powershell
dotnet run --project src/AcClient.Service
```

Terminal 3:

```powershell
dotnet run --project src/Launcher.App -- --backend http://localhost:5042 --account acc_local_demo --steam 76561190000000001 --keep-runtime
```

Terminal 4:

```powershell
$session = Get-Content runtime/session.json | ConvertFrom-Json
$token = Get-Content runtime/join-token.json | ConvertFrom-Json
dotnet run --project tools/simulators/ServerBridge.Agent -- --backend http://localhost:5042 --match $session.matchSessionId --server $session.serverId --account $session.accountId --steam $session.steamId --token $token.joinToken --simulate-cheat --runtime-sec 8
```

Reviewer workflow examples:

```powershell
dotnet run --project src/Reviewer.Console -- --backend http://localhost:5042 --internal-api-key dev-internal-api-key list-evidence --match $session.matchSessionId --account $session.accountId
dotnet run --project src/Reviewer.Console -- --backend http://localhost:5042 --internal-api-key dev-internal-api-key list-cases --status open
```

## Runbook

## Daily local verification

1. Build solution.
2. Run smoke test.
3. Confirm `/healthz` responds.
4. Confirm `data/controlplane.db` updates.
5. Confirm action feed returns expected actions for synthetic cheat mode.

## Reset local state

```powershell
Remove-Item -Recurse -Force runtime,data -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force src/ControlPlane.Api/evidence -ErrorAction SilentlyContinue
```

Then rerun API + AC to regenerate state.

## Integration Notes

For real CS2 server integration:

1. Bind your real CS2 hooks to `src/Cs2.Plugin.CounterStrikeSharp` runtime methods (or mirror this flow in `src/Cs2.Plugin.Metamod`).
2. On connection attempt:
- extract player token.
- send `X-Server-Api-Key`.
- call `/v1/attestation/validate-join`.
- reject on any `allow=false`.
3. During match:
- push tick/shot/LoS telemetry in batches.
- send `X-Server-Api-Key` on telemetry and health/action requests.
- poll `/v1/attestation/match-health`.
- consume `/v1/enforcement/actions/{matchSessionId}/pending` and ack via `/v1/enforcement/actions/ack`.

For real launcher integration:

1. Replace CLI with desktop shell.
2. Keep runtime file contract or replace with IPC.
3. Ensure launcher only starts game after AC token appears and is fresh.

For real AC integration:

1. Replace placeholder integrity booleans with actual probes.
2. Sign releases and protect service lifecycle.
3. Harden key storage and anti-tamper protections.

## Security Notes

- Do not ship with default `JoinTokenSecret`.
- Do not trust client-only detections for irreversible actions.
- Keep token TTL short.
- Enforce single-use token semantics.
- Separate behavior-based signals from high-confidence integrity/rules.
- Maintain evidence and review controls before permanent bans.

## Troubleshooting

`AC did not produce a join token`:

- Check AC service is running.
- Check `AcClient.SessionFilePath` matches launcher runtime path.
- Check backend URL alignment.

`Join denied: token_replayed`:

- Token was already consumed.
- Request a fresh queue + token.

`Join denied: token_claim_mismatch`:

- Match/server/account/steam in validation call differs from token claims.

`Join denied: stale_heartbeat`:

- Heartbeats are missing or delayed beyond grace window.

No telemetry actions generated:

- Verify telemetry endpoints are receiving sufficient sample volume.
- Verify simulated cheat mode in server bridge.

## Project Layout

```text
.
|-- ANTICHEAT_FULL_BUILD_SPEC.md
|-- Cs2AcStack.slnx
|-- README.md
|-- analytics
|   `-- detection-tuning
|-- ops
|   |-- infra
|   |-- observability
|   |-- security
|   `-- runbooks
|-- scripts
|   |-- run-controlplane.ps1
|   |-- run-ac-service.ps1
|   |-- run-launcher.ps1
|   |-- smoke-test.ps1
|   `-- reviewer-demo.ps1
|-- tools
|   `-- simulators
|       `-- ServerBridge.Agent
`-- src
    |-- Shared.Contracts
    |-- ControlPlane.Api
    |-- AcClient.Service
    |-- Launcher.App
    |-- Cs2.Plugin.CounterStrikeSharp
    |-- Cs2.Plugin.Metamod
    |-- Kernel.Driver
    |-- Kernel.Bridge
    `-- Reviewer.Console
```

## Current Scope and Roadmap

Current scope:

- End-to-end handshake and gate.
- Heartbeat health model.
- Telemetry ingest and baseline detector outputs.
- SQLite-backed persistence.
- Local run scripts.

Next production steps:

1. Replace simulated server bridge with CS2 plugin integration.
2. Add proper identity/auth and account session hardening.
3. Add evidence pack pipeline and review console.
4. Add distributed infra (message bus, metrics, tracing, horizontal scaling).
5. Add policy rollout controls and staged detector tuning.
6. Add kernel-mode component and boot-chain attestation path if required by trust tier.



