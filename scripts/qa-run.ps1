param(
    [string]$Backend = "http://localhost:5042",
    [switch]$Fast,
    [switch]$SkipReviewerDemo,
    [switch]$SkipBanLifecycle,
    [switch]$SkipPluginGatewaySmoke,
    [switch]$SkipQueueAuthSmoke,
    [switch]$SkipSessionRevokeSmoke,
    [switch]$SkipRetentionSmoke,
    [switch]$SkipPolicyHashSmoke,
    [switch]$SkipSecurityEventSmoke,
    [switch]$SkipSecurityAlertSmoke,
    [string]$ReportPath = ""
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo
$script:qaResults = @()
$script:qaFailed = $false
$script:qaError = ""

function Resolve-ReportPath {
    param([string]$PathOverride)
    if (-not [string]::IsNullOrWhiteSpace($PathOverride)) {
        if ([System.IO.Path]::IsPathRooted($PathOverride)) {
            return $PathOverride
        }

        return (Join-Path $repo $PathOverride)
    }

    $dir = Join-Path $repo "runtime\qa-reports"
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmss")
    return Join-Path $dir "qa-$stamp.json"
}

function Write-Report {
    param([string]$OutputPath)
    $report = [pscustomobject]@{
        generatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
        backend = $Backend
        fast = [bool]$Fast
        success = (-not $script:qaFailed)
        error = if ($script:qaFailed) { $script:qaError } else { $null }
        results = $script:qaResults
    }

    $reportJson = $report | ConvertTo-Json -Depth 6
    $reportDir = Split-Path -Parent $OutputPath
    if (-not [string]::IsNullOrWhiteSpace($reportDir)) {
        New-Item -ItemType Directory -Path $reportDir -Force | Out-Null
    }

    Set-Content -Path $OutputPath -Value $reportJson
    Write-Host "[qa] report: $OutputPath"
}

function Invoke-Step {
    param(
        [string]$Name,
        [scriptblock]$Action
    )

    $started = (Get-Date).ToUniversalTime()
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Write-Host "[qa] $Name"
    & $Action
    $exitCode = $LASTEXITCODE
    $sw.Stop()

    $script:qaResults += [pscustomobject]@{
        step = $Name
        ok = ($exitCode -eq 0)
        exitCode = $exitCode
        durationSec = [Math]::Round($sw.Elapsed.TotalSeconds, 2)
        startedAtUtc = $started.ToString("o")
    }

    if ($exitCode -ne 0) {
        throw "Step failed: $Name (exit=$exitCode)"
    }
}

try {
    Invoke-Step "build" { dotnet build Cs2AcStack.slnx }
    Invoke-Step "smoke-test" { powershell -ExecutionPolicy Bypass -File scripts/smoke-test.ps1 -Backend $Backend }

    if (-not $Fast -and -not $SkipReviewerDemo) {
        Invoke-Step "reviewer-demo" { powershell -ExecutionPolicy Bypass -File scripts/reviewer-demo.ps1 -Backend $Backend }
    }

    if (-not $Fast -and -not $SkipBanLifecycle) {
        Invoke-Step "smoke-ban-lifecycle" { powershell -ExecutionPolicy Bypass -File scripts/smoke-ban-lifecycle.ps1 -Backend $Backend }
    }

    if (-not $Fast -and -not $SkipPluginGatewaySmoke) {
        Invoke-Step "smoke-plugin-gateway" { powershell -ExecutionPolicy Bypass -File scripts/smoke-plugin-gateway.ps1 -Backend $Backend }
    }

    if (-not $Fast -and -not $SkipQueueAuthSmoke) {
        Invoke-Step "smoke-queue-auth" { powershell -ExecutionPolicy Bypass -File scripts/smoke-queue-auth.ps1 -Backend $Backend }
    }

    if (-not $Fast -and -not $SkipSessionRevokeSmoke) {
        Invoke-Step "smoke-session-revoke" { powershell -ExecutionPolicy Bypass -File scripts/smoke-session-revoke.ps1 -Backend $Backend }
    }

    if (-not $Fast -and -not $SkipRetentionSmoke) {
        Invoke-Step "smoke-retention-status" { powershell -ExecutionPolicy Bypass -File scripts/smoke-retention-status.ps1 -Backend $Backend }
    }

    if (-not $Fast -and -not $SkipPolicyHashSmoke) {
        Invoke-Step "smoke-policy-hash" { powershell -ExecutionPolicy Bypass -File scripts/smoke-policy-hash.ps1 -Backend $Backend }
    }

    if (-not $Fast -and -not $SkipSecurityEventSmoke) {
        Invoke-Step "smoke-security-events" { powershell -ExecutionPolicy Bypass -File scripts/smoke-security-events.ps1 -Backend $Backend }
    }

    if (-not $Fast -and -not $SkipSecurityAlertSmoke) {
        Invoke-Step "smoke-security-alert-status" { powershell -ExecutionPolicy Bypass -File scripts/smoke-security-alert-status.ps1 -Backend $Backend }
    }

    Invoke-Step "threshold-tuner-sanity" { powershell -ExecutionPolicy Bypass -File scripts/run-threshold-tuner.ps1 -MinSamples 1 -MinConfidence 0.1 }

    Write-Host "[qa] complete"
}
catch {
    $script:qaFailed = $true
    $script:qaError = $_.Exception.Message
    throw
}
finally {
    $resolvedReport = Resolve-ReportPath -PathOverride $ReportPath
    Write-Report -OutputPath $resolvedReport
}
