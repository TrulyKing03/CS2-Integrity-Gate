param(
    [string]$Backend = "http://localhost:5042"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$internalKey = if ($env:CS2IG_INTERNAL_API_KEY) { $env:CS2IG_INTERNAL_API_KEY } else { "dev-internal-api-key" }
$runtimeDir = Join-Path $repo "runtime"
$sessionPath = Join-Path $runtimeDir "session.json"
$tokenPath = Join-Path $runtimeDir "join-token.json"

if (-not (Test-Path $runtimeDir)) {
    New-Item -ItemType Directory -Path $runtimeDir | Out-Null
}
Remove-Item $sessionPath -ErrorAction SilentlyContinue
Remove-Item $tokenPath -ErrorAction SilentlyContinue

$apiJob = Start-Job -ScriptBlock {
    param($r, $u)
    Set-Location $r
    dotnet run --project src/ControlPlane.Api --urls $u
} -ArgumentList $repo, $Backend

$acJob = Start-Job -ScriptBlock {
    param($r)
    Set-Location $r
    dotnet run --project src/AcClient.Service
} -ArgumentList $repo

try {
    Start-Sleep -Seconds 8

    $runId = [Guid]::NewGuid().ToString("N").Substring(0, 10)
    $username = "qrestrict_$runId"

    Write-Host "[queue-restrict-smoke] baseline launcher (should succeed)..."
    dotnet run --project src/Launcher.App -- --backend $Backend --username $username --password "local_password" --token-wait-sec 180 --keep-runtime
    if ($LASTEXITCODE -ne 0) {
        throw "baseline launcher failed with exit code $LASTEXITCODE"
    }

    $session = Get-Content $sessionPath | ConvertFrom-Json

    Write-Host "[queue-restrict-smoke] create queue restriction..."
    dotnet run --project src/Reviewer.Console -- --backend $Backend --internal-api-key $internalKey create-queue-restriction --account $session.accountId --reason smoke_queue_restrict --duration-sec 120 --by smoke
    if ($LASTEXITCODE -ne 0) {
        throw "create-queue-restriction failed with exit code $LASTEXITCODE"
    }

    Remove-Item $sessionPath -ErrorAction SilentlyContinue
    Remove-Item $tokenPath -ErrorAction SilentlyContinue

    Write-Host "[queue-restrict-smoke] launcher while restricted (should fail with queue_restricted)..."
    dotnet run --project src/Launcher.App -- --backend $Backend --username $username --password "local_password" --token-wait-sec 30 --keep-runtime
    if ($LASTEXITCODE -eq 0) {
        throw "expected launcher queue request to fail while queue restriction is active"
    }

    Write-Host "[queue-restrict-smoke] list active restrictions..."
    $restrictionsJson = dotnet run --project src/Reviewer.Console -- --backend $Backend --internal-api-key $internalKey list-queue-restrictions --account $session.accountId --status active
    if ($LASTEXITCODE -ne 0) {
        throw "list-queue-restrictions failed with exit code $LASTEXITCODE"
    }

    $restrictions = $restrictionsJson | ConvertFrom-Json
    if ($null -eq $restrictions -or $restrictions.Count -lt 1) {
        throw "expected at least one active queue restriction"
    }

    Write-Host "[queue-restrict-smoke] complete"
}
finally {
    Stop-Job -Job $acJob -ErrorAction SilentlyContinue
    Stop-Job -Job $apiJob -ErrorAction SilentlyContinue
    Remove-Job -Job $acJob, $apiJob -ErrorAction SilentlyContinue
}
