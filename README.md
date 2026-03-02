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

This repo contains five .NET projects plus scripts:

- `src/ControlPlane.Api`: backend control plane API.
- `src/AcClient.Service`: local anti-cheat service process.
- `src/Launcher.Cli`: launcher orchestration CLI.
- `src/ServerBridge.Agent`: server integration simulator/bridge.
- `src/Shared.Contracts`: strongly typed shared contracts.
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

## `Launcher.Cli`

Main capabilities:

- Queue orchestration and runtime session write.
- Waits for a valid join token generated by AC.
- Prints CS2 launch command with join token.
- Optional `--self-validate` exists for diagnostics only.

## `ServerBridge.Agent`

Main capabilities:

- Simulates server-side gate behavior.
- Validates join token like a real plugin should.
- Sends synthetic telemetry.
- Reads health and enforcement action streams.

Use this as the protocol reference adapter before binding to a real CS2 plugin.

## `Shared.Contracts`

Central shared DTO contract library used by all services:

- Auth/queue DTOs.
- Attestation and heartbeat DTOs.
- Join validation DTOs.
- Telemetry DTOs (`TickPlayerState`, `ShotEvent`, `LosSample`).
- Detection score and enforcement action DTOs.

## API Surface

From `src/ControlPlane.Api/Program.cs`.

System:

- `GET /`
- `GET /healthz`

Auth + Queue:

- `POST /v1/auth/login`
- `POST /v1/queue/enqueue`

Attestation:

- `POST /v1/attestation/enroll`
- `POST /v1/attestation/match-start`
- `POST /v1/attestation/heartbeat`
- `POST /v1/attestation/validate-join`
- `GET /v1/attestation/match-health?matchSessionId=...`

Telemetry:

- `POST /v1/telemetry/ticks`
- `POST /v1/telemetry/shots`
- `POST /v1/telemetry/los`

Detection/Enforcement read APIs:

- `GET /v1/detections/scores/{matchSessionId}/{accountId}`
- `GET /v1/enforcement/actions/{matchSessionId}`

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

Data captured:

- identity bindings.
- session allocation.
- token issuance/use.
- heartbeat history.
- telemetry event stream.
- per-channel suspicion scores.
- enforcement action records.

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

Production notes:

- Replace `JoinTokenSecret` with a long random secret before any real deployment.
- Move secrets to environment variables or secret manager.

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
dotnet run --project src/Launcher.Cli -- --backend http://localhost:5042 --account acc_local_demo --steam 76561190000000001 --keep-runtime
```

Terminal 4:

```powershell
$session = Get-Content runtime/session.json | ConvertFrom-Json
$token = Get-Content runtime/join-token.json | ConvertFrom-Json
dotnet run --project src/ServerBridge.Agent -- --backend http://localhost:5042 --match $session.matchSessionId --server $session.serverId --account $session.accountId --steam $session.steamId --token $token.joinToken --simulate-cheat --runtime-sec 8
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
```

Then rerun API + AC to regenerate state.

## Integration Notes

For real CS2 server integration:

1. Replace `ServerBridge.Agent` with your actual plugin/adapter.
2. On connection attempt:
- extract player token.
- call `/v1/attestation/validate-join`.
- reject on any `allow=false`.
3. During match:
- push tick/shot/LoS telemetry in batches.
- poll `/v1/attestation/match-health`.
- consume `/v1/enforcement/actions/{matchSessionId}`.

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
|-- scripts
|   |-- run-controlplane.ps1
|   |-- run-ac-service.ps1
|   |-- run-launcher.ps1
|   `-- smoke-test.ps1
`-- src
    |-- Shared.Contracts
    |-- ControlPlane.Api
    |-- AcClient.Service
    |-- Launcher.Cli
    `-- ServerBridge.Agent
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
