param(
    [string]$Backend = "http://localhost:5042"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$internalKey = if ($env:CS2IG_INTERNAL_API_KEY) { $env:CS2IG_INTERNAL_API_KEY } else { "dev-internal-api-key" }
$serverKey = if ($env:CS2IG_SERVER_API_KEY) { $env:CS2IG_SERVER_API_KEY } else { "dev-server-api-key" }

$apiJob = Start-Job -ScriptBlock {
    param($r, $u)
    Set-Location $r
    $env:SecurityAlerts__Enabled = "false"
    $env:SecurityAlerts__WindowMinutes = "15"
    $env:SecurityAlerts__MediumThreshold = "9999"
    $env:SecurityAlerts__HighThreshold = "1"
    $env:SecurityAlerts__CooldownMinutes = "1"
    dotnet run --project src/ControlPlane.Api --urls $u
} -ArgumentList $repo, $Backend

try {
    Start-Sleep -Seconds 8

    Write-Host "[security-alert-manual-smoke] trigger high-severity event..."
    $headers = @{ "X-Server-Api-Key" = $serverKey }
    $body = @{
        serverId = "srv_eu_01"
        steamId = "76561190000000001"
        accountId = "acc_alert_manual_smoke"
        matchSessionId = "ms_alert_manual_smoke"
        joinToken = "invalid.token.value"
    } | ConvertTo-Json
    $response = Invoke-RestMethod -Method Post -Uri "$Backend/v1/attestation/validate-join" -Headers $headers -ContentType "application/json" -Body $body
    if ($response.allow -ne $false) {
        throw "expected validate-join to reject invalid token"
    }

    Write-Host "[security-alert-manual-smoke] evaluate without force (alerts disabled)..."
    $evalNoForceJson = dotnet run --project src/Reviewer.Console -- --backend $Backend --internal-api-key $internalKey run-security-alert-eval
    if ($LASTEXITCODE -ne 0) {
        throw "run-security-alert-eval (no force) failed with exit code $LASTEXITCODE"
    }

    $evalNoForce = $evalNoForceJson | ConvertFrom-Json
    if (-not $evalNoForce.disabled) {
        throw "expected evaluation to report disabled=true when alerts are disabled"
    }
    if ($evalNoForce.alertTriggered) {
        throw "expected alertTriggered=false when force is not used and alerts are disabled"
    }

    Write-Host "[security-alert-manual-smoke] evaluate with force..."
    $evalForceJson = dotnet run --project src/Reviewer.Console -- --backend $Backend --internal-api-key $internalKey run-security-alert-eval --force
    if ($LASTEXITCODE -ne 0) {
        throw "run-security-alert-eval --force failed with exit code $LASTEXITCODE"
    }

    $evalForce = $evalForceJson | ConvertFrom-Json
    if (-not $evalForce.alertTriggered) {
        throw "expected forced evaluation to trigger an alert"
    }
    if ($evalForce.alertLevel -ne "high") {
        throw "expected forced alert level to be high, got $($evalForce.alertLevel)"
    }

    Write-Host "[security-alert-manual-smoke] check alert status..."
    $statusJson = dotnet run --project src/Reviewer.Console -- --backend $Backend --internal-api-key $internalKey security-alert-status
    if ($LASTEXITCODE -ne 0) {
        throw "security-alert-status command failed with exit code $LASTEXITCODE"
    }

    $status = $statusJson | ConvertFrom-Json
    if ($null -eq $status.lastAlertAtUtc) {
        throw "security-alert-status did not report lastAlertAtUtc"
    }
    if ($status.lastAlertLevel -ne "high") {
        throw "security-alert-status expected high level, got $($status.lastAlertLevel)"
    }

    Write-Host "[security-alert-manual-smoke] complete"
}
finally {
    Stop-Job -Job $apiJob -ErrorAction SilentlyContinue
    Remove-Job -Job $apiJob -ErrorAction SilentlyContinue
}
