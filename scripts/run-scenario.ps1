param(
    [string]$Scenario = "smoke",
    [string]$SettingsPath = "ops/stack.settings.sample.json"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$settingsAbsolute = if ([System.IO.Path]::IsPathRooted($SettingsPath)) { $SettingsPath } else { Join-Path $repo $SettingsPath }
if (-not (Test-Path $settingsAbsolute)) {
    throw "Settings file not found: $settingsAbsolute"
}

$settings = Get-Content $settingsAbsolute | ConvertFrom-Json
$backend = if ($settings.backendBaseUrl) { $settings.backendBaseUrl } else { "http://localhost:5042" }
$bridgeApiKey = if ($settings.bridgeApiKey) { $settings.bridgeApiKey } else { "dev-bridge-api-key" }
$env:CS2IG_SERVER_API_KEY = if ($settings.serverApiKey) { $settings.serverApiKey } else { "dev-server-api-key" }
$env:CS2IG_INTERNAL_API_KEY = if ($settings.internalApiKey) { $settings.internalApiKey } else { "dev-internal-api-key" }
if ($settings.runtimeSigningKey) {
    $env:CS2IG_RUNTIME_SIGNING_KEY = $settings.runtimeSigningKey
}

switch ($Scenario.ToLowerInvariant()) {
    "smoke" {
        powershell -ExecutionPolicy Bypass -File scripts/smoke-test.ps1 -Backend $backend
        break
    }
    "reviewer" {
        powershell -ExecutionPolicy Bypass -File scripts/reviewer-demo.ps1 -Backend $backend
        break
    }
    "ban" {
        powershell -ExecutionPolicy Bypass -File scripts/smoke-ban-lifecycle.ps1 -Backend $backend
        break
    }
    "qa-fast" {
        powershell -ExecutionPolicy Bypass -File scripts/qa-run.ps1 -Backend $backend -Fast
        break
    }
    "gateway" {
        powershell -ExecutionPolicy Bypass -File scripts/smoke-plugin-gateway.ps1 -Backend $backend -BridgeApiKey $bridgeApiKey
        break
    }
    "qa-full" {
        powershell -ExecutionPolicy Bypass -File scripts/qa-run.ps1 -Backend $backend
        break
    }
    default {
        throw "Unknown scenario: $Scenario (expected smoke|reviewer|ban|gateway|qa-fast|qa-full)"
    }
}

if ($LASTEXITCODE -ne 0) {
    throw "Scenario failed with exit code $LASTEXITCODE"
}

Write-Host "Scenario completed: $Scenario"
