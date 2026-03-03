# PluginBridge.Gateway

`PluginBridge.Gateway` is a local HTTP integration layer between a real CS2 plugin host and the shared `Cs2.Plugin.CounterStrikeSharp` runtime.

Use this when you want your CounterStrikeSharp/Metamod wrapper to send game events over HTTP without embedding backend/telemetry logic directly into the host plugin.

## What It Does

- Validates join attempts through runtime/backend.
- Tracks connected/disconnected players and starts per-match health/action workers.
- Accepts tick/shot/visibility batches and forwards telemetry through runtime buffering.
- Queues host enforcement actions so your plugin can consume and apply them.

## Start

```powershell
dotnet run --project tools/adapters/PluginBridge.Gateway
```

Default listen URL: `http://localhost:5055`

Required header on all `/v1/plugin/*` routes:

- `X-Bridge-Api-Key: dev-bridge-api-key` (or your configured value)

## Core Endpoints

- `POST /v1/plugin/connect-attempt`
- `POST /v1/plugin/connected`
- `POST /v1/plugin/disconnected`
- `POST /v1/plugin/ticks`
- `POST /v1/plugin/shots`
- `POST /v1/plugin/visibility`
- `POST /v1/plugin/flush`
- `POST /v1/plugin/host-actions/consume`

## Example Request

```powershell
$headers = @{ "X-Bridge-Api-Key" = "dev-bridge-api-key" }
$body = @{
  matchSessionId = "ms_demo"
  serverId = "srv_eu_01"
  accountId = "acc_demo"
  steamId = "76561190000000001"
  joinToken = "token_here"
} | ConvertTo-Json

Invoke-RestMethod `
  -Method Post `
  -Uri "http://localhost:5055/v1/plugin/connect-attempt" `
  -Headers $headers `
  -ContentType "application/json" `
  -Body $body
```
