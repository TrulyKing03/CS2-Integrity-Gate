# Cs2.Plugin.CounterStrikeSharp

This project now contains a production-oriented adapter runtime scaffold for real CS2 server integration.

Included:

- `PluginRuntimeOptions`: backend/auth/telemetry runtime options.
- `ControlPlanePluginClient`: typed HTTP client for join validation, health, telemetry, action polling, and action ack.
- `PluginRuntime`: orchestration layer for:
  - join validation and allow/deny handling,
  - telemetry buffering and flush,
  - health polling and action application,
  - pending-action polling with acknowledgments.
- `IPluginHostBridge`: abstraction for game-server actions (deny/accept/apply/log).
- `CounterStrikeSharpAdapterSkeleton`: host-facing call surface for real CounterStrikeSharp event hooks.

This code intentionally avoids direct CounterStrikeSharp package references so it can compile in CI and local development before CS2 server binding is added.
