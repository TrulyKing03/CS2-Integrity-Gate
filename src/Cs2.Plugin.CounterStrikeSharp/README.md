# Cs2.Plugin.CounterStrikeSharp

This project now contains a production-oriented adapter runtime scaffold for real CS2 server integration.

Included:

- `PluginRuntimeOptions`: backend/auth/telemetry runtime options.
- `ControlPlanePluginClient`: typed HTTP client for join validation, health, telemetry, action polling, and action ack.
- `PluginRuntime`: orchestration layer for:
  - join validation and allow/deny handling,
  - telemetry buffering and flush,
  - health polling and action application,
  - pending-action polling with acknowledgments (including failed-apply acks).
- `MatchRuntimeCoordinator`: per-match background worker that runs health/action poll loops while players are connected.
- Final telemetry flush on worker shutdown to reduce end-of-match data loss.
- `IPluginHostBridge`: abstraction for game-server actions (deny/accept/apply/log).
- `CounterStrikeSharpAdapterSkeleton`: host-facing call surface for real CounterStrikeSharp event hooks.

This code intentionally avoids direct CounterStrikeSharp package references so it can compile in CI and local development before CS2 server binding is added.

Typical host flow:

1. Create `PluginRuntime` + `MatchRuntimeCoordinator`.
2. On connect attempt: call `OnPlayerConnectAttemptAsync`.
3. When player is accepted and fully connected in your host: call `OnPlayerConnected`.
4. Feed ticks/shots/visibility to the adapter.
5. On disconnect: call `OnPlayerDisconnectedAsync`.
