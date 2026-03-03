param(
    [string]$Backend = "http://localhost:5042"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo
$internalKey = if ($env:CS2IG_INTERNAL_API_KEY) { $env:CS2IG_INTERNAL_API_KEY } else { "dev-internal-api-key" }

$apiJob = Start-Job -ScriptBlock {
    param($r, $u)
    Set-Location $r
    dotnet run --project src/ControlPlane.Api --urls $u
} -ArgumentList $repo, $Backend

try {
    Start-Sleep -Seconds 8

    Write-Host "[security-smoke] force unauthorized internal call..."
    $failedAsExpected = $false
    try {
        dotnet run --project src/Reviewer.Console -- --backend $Backend --internal-api-key wrong-key system-metrics *> $null
    }
    catch {
        $failedAsExpected = $true
    }

    if (-not $failedAsExpected -and $LASTEXITCODE -eq 0) {
        throw "expected system-metrics with invalid key to fail"
    }

    Write-Host "[security-smoke] query security summary..."
    $summaryJson = dotnet run --project src/Reviewer.Console -- --backend $Backend --internal-api-key $internalKey security-summary --since-minutes 30
    if ($LASTEXITCODE -ne 0) {
        throw "security-summary command failed with exit code $LASTEXITCODE"
    }

    $summary = $summaryJson | ConvertFrom-Json
    if ($null -eq $summary -or $summary.Count -lt 1) {
        throw "security summary is empty"
    }

    $hasUnauthorized = $false
    foreach ($row in $summary) {
        if ($row.eventType -eq "unauthorized_response") {
            $hasUnauthorized = $true
            break
        }
    }

    if (-not $hasUnauthorized) {
        throw "unauthorized_response was not present in security summary"
    }

    Write-Host "[security-smoke] query detailed events..."
    $eventsJson = dotnet run --project src/Reviewer.Console -- --backend $Backend --internal-api-key $internalKey list-security-events --since-minutes 30 --limit 25
    if ($LASTEXITCODE -ne 0) {
        throw "list-security-events command failed with exit code $LASTEXITCODE"
    }

    $events = $eventsJson | ConvertFrom-Json
    if ($null -eq $events -or $events.Count -lt 1) {
        throw "no security events returned"
    }

    Write-Host "[security-smoke] complete"
}
finally {
    Stop-Job -Job $apiJob -ErrorAction SilentlyContinue
    Remove-Job -Job $apiJob -ErrorAction SilentlyContinue
}
