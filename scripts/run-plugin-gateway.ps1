param(
    [string]$Backend = "http://localhost:5042",
    [string]$ListenUrl = "http://localhost:5055",
    [string]$BridgeApiKey = "dev-bridge-api-key",
    [string]$ServerApiKey = "dev-server-api-key"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$env:Gateway__BackendBaseUrl = $Backend
$env:Gateway__ListenUrl = $ListenUrl
$env:Gateway__BridgeApiKey = $BridgeApiKey
$env:Gateway__ServerApiKey = $ServerApiKey

Write-Host "[gateway] backend=$Backend"
Write-Host "[gateway] listen=$ListenUrl"
Write-Host "[gateway] bridgeApiKey set"
Write-Host "[gateway] serverApiKey set"

dotnet run --project tools/adapters/PluginBridge.Gateway
