param(
    [string]$Backend = "http://localhost:5042",
    [switch]$Fast,
    [switch]$SkipReviewerDemo,
    [switch]$SkipBanLifecycle
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

function Invoke-Step {
    param(
        [string]$Name,
        [scriptblock]$Action
    )

    Write-Host "[qa] $Name"
    & $Action
    if ($LASTEXITCODE -ne 0) {
        throw "Step failed: $Name (exit=$LASTEXITCODE)"
    }
}

Invoke-Step "build" { dotnet build Cs2AcStack.slnx }
Invoke-Step "smoke-test" { powershell -ExecutionPolicy Bypass -File scripts/smoke-test.ps1 -Backend $Backend }

if (-not $Fast -and -not $SkipReviewerDemo) {
    Invoke-Step "reviewer-demo" { powershell -ExecutionPolicy Bypass -File scripts/reviewer-demo.ps1 -Backend $Backend }
}

if (-not $Fast -and -not $SkipBanLifecycle) {
    Invoke-Step "smoke-ban-lifecycle" { powershell -ExecutionPolicy Bypass -File scripts/smoke-ban-lifecycle.ps1 -Backend $Backend }
}

Invoke-Step "threshold-tuner-sanity" { powershell -ExecutionPolicy Bypass -File scripts/run-threshold-tuner.ps1 -MinSamples 1 -MinConfidence 0.1 }

Write-Host "[qa] complete"
