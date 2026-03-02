# CS2 Standalone Launcher + Anti-Cheat Full Build Spec

## 1) Scope and Assumptions
- Integration model: custom integration (easy server/plugin integration).
- Telemetry available from game/server side:
  - Per-tick player positions/velocities: yes.
  - View angles: yes.
  - Bullet impacts/traces: yes.
  - Server raycasts for LoS: yes.
- Matchmaking model: own queue and owned server fleet.
- Target: high-integrity hard gate (launcher + required AC + server validation), with conservative false-positive handling.

## 2) System Architecture

## 2.1 Components
1. `Launcher` (desktop app)
- Login, Steam link, updates, AC lifecycle checks, queue UI, CS2 launch orchestration.

2. `AC Client Service` (user-mode service)
- Device key management, signed heartbeat emission, local integrity checks, anti-tamper watchdog, policy execution.

3. `AC Kernel Driver` (optional for casual, required for high-trust queue)
- Boot-time integrity signal collection, kernel integrity telemetry, protected anti-tamper signals.

4. `Auth Service`
- User auth, Steam account binding, session issuance.

5. `Matchmaking Service`
- Queue management, match creation, player slot assignment, server reservation.

6. `Attestation Service`
- Validates attestation packets and heartbeats, computes integrity status, issues short-lived join tokens.

7. `Policy Engine`
- Versioned policy bundles (requirements and response rules), remote rollout by queue/region.

8. `Game Integrity Service`
- Ingests server telemetry, computes behavioral/rule detections, updates suspicion channels.

9. `Enforcement Service`
- Applies kick/cooldown/trust-tier actions and ban decisions under policy.

10. `Evidence Service`
- Stores signed evidence packs, reviewer metadata, audit logs, appeal status.

11. `Review Console`
- Internal tooling for manual review and appeals.

## 2.2 High-level Data Flow
1. Launcher authenticates user and starts AC service.
2. Player queues for match; matchmaking allocates server + `match_session_id`.
3. AC submits attestation to Attestation Service.
4. Attestation Service issues one-time `join_token` (short TTL).
5. Launcher starts CS2 with connection payload.
6. Server validates token with Attestation Service before admitting player.
7. During match: AC heartbeats + server telemetry stream continuously.
8. Game Integrity + Enforcement produce actions and evidence.
9. Evidence and decisions flow to review and appeals.

## 3) Trust Model
- Server authority is source of truth for movement/combat state.
- Client AC is required for queue access and ongoing session validity.
- Kernel telemetry increases confidence but is not the sole ban basis.
- Permanent bans require:
  - high-confidence impossible state or confirmed integrity tamper, or
  - multi-match, multi-signal behavioral consensus with manual review.

## 4) Protocols and Message Formats

## 4.1 Common Envelope (all signed client messages)
```json
{
  "msg_type": "heartbeat_v1",
  "msg_id": "uuid",
  "issued_at": "2026-03-02T13:45:11Z",
  "client_version": "1.4.2",
  "device_id": "did_...",
  "account_id": "acc_...",
  "payload": {},
  "sig": "base64_ed25519_signature"
}
```

## 4.2 Enrollment
Endpoint: `POST /v1/attestation/enroll`

Request:
```json
{
  "account_id": "acc_...",
  "steam_id": "7656...",
  "device_pubkey": "base64_ed25519_pubkey",
  "launcher_version": "1.4.2",
  "ac_version": "2.1.0"
}
```

Response:
```json
{
  "device_id": "did_...",
  "device_cert": "short-lived signed cert",
  "policy_version": "pol_2026_03_01_01",
  "requirements_tier": "A"
}
```

## 4.3 Match Attestation (pre-join)
Endpoint: `POST /v1/attestation/match-start`

Request:
```json
{
  "match_session_id": "ms_...",
  "account_id": "acc_...",
  "steam_id": "7656...",
  "device_id": "did_...",
  "nonce": "random_128bit",
  "platform_signals": {
    "secure_boot": true,
    "tpm_2_0": true,
    "iommu": true,
    "vbs": false
  },
  "integrity_signals": {
    "ac_service_healthy": true,
    "driver_loaded": true,
    "module_policy_ok": true
  }
}
```

Response:
```json
{
  "join_token": "jwt_or_paseto",
  "join_token_ttl_sec": 90,
  "heartbeat_interval_sec": 10,
  "grace_window_sec": 60,
  "queue_tier": "high_trust"
}
```

`join_token` claims:
- `sub`: `account_id`
- `steam_id`
- `match_session_id`
- `server_id`
- `device_id`
- `iat`, `exp`
- `jti` (single-use id)

## 4.4 Continuous Heartbeat
Endpoint: `POST /v1/attestation/heartbeat`

Request:
```json
{
  "match_session_id": "ms_...",
  "account_id": "acc_...",
  "device_id": "did_...",
  "seq": 1223,
  "platform_signals": {
    "secure_boot": true,
    "tpm_2_0": true,
    "iommu": true,
    "vbs": false
  },
  "integrity_signals": {
    "ac_service_healthy": true,
    "driver_loaded": true,
    "anti_tamper_ok": true,
    "policy_hash": "sha256:..."
  },
  "perf": {
    "heartbeat_latency_ms": 42
  }
}
```

Response:
```json
{
  "status": "healthy",
  "next_interval_sec": 10,
  "server_action_hint": "none"
}
```

## 4.5 Server Join Validation
Endpoint: `POST /v1/attestation/validate-join`

Request:
```json
{
  "server_id": "srv_eu_01",
  "steam_id": "7656...",
  "account_id": "acc_...",
  "match_session_id": "ms_...",
  "join_token": "..."
}
```

Response:
```json
{
  "allow": true,
  "reason": "ok",
  "heartbeat_status": "healthy",
  "trust_tier": "high_trust"
}
```

## 4.6 Server Health Poll/Push
- Pull endpoint: `GET /v1/attestation/match-health?match_session_id=...`
- Optional push: WebSocket stream of `player_unhealthy`, `player_recovered`, `action_required`.

## 5) Database Schema (Core Tables)

1. `accounts`
- `account_id` (pk), `created_at`, `status`, `risk_level`

2. `steam_links`
- `account_id` (pk/fk), `steam_id` (unique), `linked_at`, `link_status`

3. `devices`
- `device_id` (pk), `account_id` (fk), `device_pubkey`, `first_seen_at`, `last_seen_at`, `status`

4. `device_posture`
- `device_id` (fk), `snapshot_ts`, `secure_boot`, `tpm_2_0`, `iommu`, `vbs`, `driver_loaded`, `policy_hash`

5. `match_sessions`
- `match_session_id` (pk), `queue_type`, `region`, `server_id`, `created_at`, `started_at`, `ended_at`, `status`

6. `match_players`
- `match_session_id` + `account_id` (pk), `steam_id`, `team`, `join_ts`, `leave_ts`, `leave_reason`, `trust_tier`

7. `join_tokens`
- `jti` (pk), `match_session_id`, `account_id`, `device_id`, `issued_at`, `expires_at`, `used_at`, `used_by_server`

8. `heartbeats`
- `match_session_id`, `account_id`, `seq`, `received_at`, `status`, `latency_ms`, `signals_json`
- Partition by day/week.

9. `telemetry_ticks`
- `match_session_id`, `tick_id`, `player_id`, `pos`, `vel`, `view_angles`, `stance`, `weapon_state`, `net_stats`
- High-volume columnar store recommended.

10. `telemetry_events`
- `event_id` (pk), `match_session_id`, `tick_id`, `event_type` (`shot`, `hit`, `kill`, `impact`, `reload`, ...)
- `event_payload` (jsonb)

11. `detection_features`
- `feature_id`, `match_session_id`, `account_id`, `channel` (`aim`,`trigger`,`info`,`movement`,`rules`,`integrity`,`network`)
- `feature_name`, `value`, `window_start`, `window_end`, `sample_size`

12. `suspicion_scores`
- `match_session_id`, `account_id`, `channel`, `score_0_100`, `confidence_0_1`, `updated_at`

13. `enforcement_actions`
- `action_id` (pk), `account_id`, `match_session_id`, `action_type`, `duration_sec`, `reason_code`, `created_by`, `created_at`

14. `bans`
- `ban_id` (pk), `account_id`, `scope` (`queue`,`global`), `start_ts`, `end_ts`, `reason`, `status`

15. `evidence_packs`
- `evidence_id` (pk), `account_id`, `match_session_id`, `trigger_type`, `storage_uri`, `hash`, `created_at`, `review_status`

16. `appeals`
- `appeal_id` (pk), `ban_id`, `account_id`, `submitted_at`, `status`, `reviewer_id`, `decision_at`, `decision_notes`

17. `audit_log`
- `audit_id`, `actor_type`, `actor_id`, `action`, `target_id`, `ts`, `meta_json`

## 6) Server Plugin Module List

1. `ModuleAuthGate`
- Validates join token via backend.
- Rejects missing/expired/replayed token.

2. `ModuleHeartbeatEnforcer`
- Tracks backend health state.
- Applies policy: warn -> spectate/kick on missing heartbeat over grace.

3. `ModuleRuleValidator`
- Impossible state checks:
  - fire cadence over weapon limits + tick tolerance,
  - invalid ammo/reload combinations,
  - impossible movement deltas/speeds.

4. `ModuleAimAnalyzer`
- Features: time-to-align, settle-time, angular velocity profiles, over/undershoot patterns.

5. `ModuleTriggerAnalyzer`
- Features: on-target duration before shot, variance, repeated near-zero reaction signature.

6. `ModuleInfoAdvantageAnalyzer`
- LoS-based visibility model + pre-aim and rotate prediction beyond plausible info.

7. `ModuleMovementAnalyzer`
- Bhop timing precision, autostrafe efficiency consistency, counter-strafe stop-time variance.

8. `ModuleNetworkCorrelation`
- Correlates suspicious outcomes with choke/loss spikes; low-confidence channel only.

9. `ModuleScoring`
- Combines channel scores, confidence, and sample size into action recommendations.

10. `ModuleEvidenceRecorder`
- Stores pre/post windows and feature snapshots for any high-severity trigger.

11. `ModulePolicySync`
- Pulls policy/version updates from backend at match start and interval.

## 7) Telemetry Schema (Server -> Backend)

Transport:
- Real-time stream (gRPC/Kafka) for live detections.
- Batch flush at round-end/match-end for full fidelity archives.

Per-tick record (`tick_player_state_v1`):
```json
{
  "match_session_id": "ms_...",
  "tick_id": 441122,
  "tick_ts": "2026-03-02T13:50:11.210Z",
  "player_id": "p_...",
  "team": "T",
  "pos": [1.12, 9.11, 0.21],
  "vel": [0.02, 0.00, -0.11],
  "view_angles": {"yaw": 122.4, "pitch": -1.2},
  "stance": "standing",
  "weapon": {"id": "ak47", "ammo_clip": 23, "is_reloading": false},
  "net": {"ping_ms": 38, "loss_pct": 0.2, "choke_pct": 0.0}
}
```

Shot event (`shot_event_v1`):
```json
{
  "match_session_id": "ms_...",
  "tick_id": 441133,
  "shooter_id": "p_...",
  "weapon_id": "ak47",
  "server_recoil_index": 4,
  "view_angles_at_shot": {"yaw": 120.1, "pitch": -1.0},
  "trace": {
    "origin": [..],
    "direction": [..],
    "hit_player_id": "p_enemy_..."
  }
}
```

Visibility sample (`los_sample_v1`):
```json
{
  "match_session_id": "ms_...",
  "tick_id": 441130,
  "observer_id": "p_...",
  "target_id": "p_enemy_...",
  "line_of_sight": false,
  "audible_proxy": false,
  "distance_m": 18.4
}
```

## 8) Scoring Model and Conservative Defaults

## 8.1 Channel Weights (initial)
- `integrity`: 1.00
- `rules`: 1.00
- `aim`: 0.60
- `trigger`: 0.60
- `info`: 0.50
- `movement`: 0.45
- `network`: 0.20

## 8.2 Minimum Sample Sizes
- Aim/trigger: >= 30 engagements.
- Info advantage: >= 12 rounds and >= 25 relevant visibility opportunities.
- Movement: >= 80 jumps/strafes where applicable.
- Network: >= 5 rounds.

## 8.3 Confidence Gates
- `score < 60`: log only.
- `60-74`: low-trust queue candidate.
- `75-84`: action candidate (cooldown/review).
- `>=85`: high severity, requires either integrity/rules support or manual confirmation before ban.

## 8.4 Auto-action Rules
1. Immediate kick:
- join token invalid/replayed.
- heartbeat absent beyond grace window.
- impossible state confirmed in-match (severe).

2. Temporary cooldown (15m-24h):
- repeated heartbeat failures or repeated severe rule violations.

3. Automatic ban:
- confirmed integrity tamper with cryptographic proof, or
- repeated impossible-state evidence across matches.

4. Manual-review-first:
- aim/trigger/info/movement-only cases without integrity/rule corroboration.

## 9) Tuning and Rollout Plan

1. Phase 0 (2-4 weeks): Observe only
- Collect all telemetry, compute scores, no player-facing penalties except hard AC gate.

2. Phase 1 (2-3 weeks): Soft enforcement
- Enable kicks for missing heartbeat and obvious impossible states.
- Behavioral channels only influence trust-tier downgrades.

3. Phase 2: Hybrid enforcement
- Cooldowns for repeated medium/high confidence detections.
- Manual review queue on all high-confidence behavior cases.

4. Phase 3: Full enforcement
- Enable automatic bans only for integrity/rules channels with strong evidence.
- Keep behavioral-only permanent bans behind reviewer approval.

Monitoring KPIs:
- False positive appeal overturn rate.
- Detection-to-action latency.
- Match abandonment impact.
- High-trust queue retention.

## 10) Evidence Pack Format

`evidence_pack_v1` contents:
1. `manifest.json`
- ids, timestamps, policy version, detector versions, hashes.

2. `window_ticks.parquet`
- 10-30s pre/post trigger player states.

3. `events.jsonl`
- shots, hits, kills, reloads, impacts, LoS samples.

4. `feature_snapshot.json`
- computed features, z-scores, percentile vs baseline, confidence.

5. `attestation_snapshot.json`
- heartbeat history, platform/integrity signals in same window.

6. `action_rationale.txt`
- concise human-readable explanation with reason codes.

Evidence storage:
- Object storage with immutable retention bucket and hash verification.

## 11) Appeal Workflow

1. Ban or major action generated with `evidence_id`.
2. Player submits appeal linked to `ban_id`.
3. Case assigned by queue/region expertise.
4. Reviewer checks:
- integrity proofs,
- impossible-state certainty,
- behavioral sample sufficiency and confounders (latency, spectator edge cases).
5. Decision outcomes:
- uphold,
- reduce penalty,
- overturn + model feedback tag.
6. Overturns feed detector calibration and threshold updates.

SLA recommendation:
- Cooldown appeals: <= 24h.
- Ban appeals: <= 72h first decision.

## 12) “Detect X by scanning Y memory structure” Guidance
- Avoid single fixed memory-structure scans as primary detection:
  - they are fragile across updates,
  - can increase false positives,
  - encourage signature chasing.
- Preferred approach:
  - use signed policy-based integrity signals,
  - module trust evaluation (signed/known/unknown),
  - anti-tamper state changes,
  - correlate with server-authoritative behavior.
- If specific memory integrity checks are used, keep them versioned, heavily QA-tested, and never sole ban criteria.

## 13) Competitive AC Mapping (Public Patterns)
- Launcher gate -> `Launcher + Auth + Matchmaking + join token`.
- Required AC presence -> `Attestation Service + heartbeat enforcement`.
- Server communication bridge -> `ModuleAuthGate + ModuleHeartbeatEnforcer`.
- Platform security posture -> Tiered requirements (`Secure Boot`, `TPM`, optional `IOMMU/VBS` for high trust).
- Integrity-first actioning -> integrity/rules channels prioritized over behavioral-only bans.

## 14) Implementation Backlog (Suggested Order)
1. Build auth/linking + queue + join token path.
2. Implement AC heartbeat and server hard gate.
3. Add impossible-state validators and evidence recorder.
4. Stand up review console and appeal flow.
5. Deploy aim/trigger/info/movement analyzers in observe mode.
6. Tune thresholds with real match baselines, then phase into enforcement.

## 15) Security and Privacy Baselines
- Minimize PII and avoid storing raw hardware serials when not required.
- Encrypt in transit and at rest.
- Signed updates for launcher/service/driver.
- Full audit trail for enforcement and reviewer actions.
- Publish player-facing policy and appeal criteria for transparency.
