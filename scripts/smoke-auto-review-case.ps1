param(
    [string]$Backend = "http://localhost:5042"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$internalKey = if ($env:CS2IG_INTERNAL_API_KEY) { $env:CS2IG_INTERNAL_API_KEY } else { "dev-internal-api-key" }
$serverKey = if ($env:CS2IG_SERVER_API_KEY) { $env:CS2IG_SERVER_API_KEY } else { "dev-server-api-key" }
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
    $username = "autoreview_$runId"

    Write-Host "[auto-review-smoke] launcher..."
    dotnet run --project src/Launcher.App -- --backend $Backend --username $username --password "local_password" --token-wait-sec 180 --keep-runtime
    if ($LASTEXITCODE -ne 0) {
        throw "launcher failed with exit code $LASTEXITCODE"
    }

    $session = Get-Content $sessionPath | ConvertFrom-Json
    $token = Get-Content $tokenPath | ConvertFrom-Json

    Write-Host "[auto-review-smoke] server bridge simulated cheat..."
    dotnet run --project tools/simulators/ServerBridge.Agent -- --backend $Backend --server-api-key $serverKey --match $session.matchSessionId --server $session.serverId --account $session.accountId --steam $session.steamId --token $token.joinToken --simulate-cheat --runtime-sec 6
    if ($LASTEXITCODE -ne 0) {
        throw "server bridge failed with exit code $LASTEXITCODE"
    }

    Write-Host "[auto-review-smoke] query review cases..."
    $casesJson = dotnet run --project src/Reviewer.Console -- --backend $Backend --internal-api-key $internalKey list-cases --status open --match $session.matchSessionId --account $session.accountId
    if ($LASTEXITCODE -ne 0) {
        throw "list-cases failed with exit code $LASTEXITCODE"
    }

    $cases = $casesJson | ConvertFrom-Json
    if ($null -eq $cases -or $cases.Count -lt 1) {
        throw "expected at least one auto-created review case"
    }

    $matched = $false
    foreach ($case in $cases) {
        if ($case.reasonCode -eq "rules_impossible_state" -or $case.reasonCode -eq "rules_fire_cadence") {
            $matched = $true
            break
        }
    }

    if (-not $matched) {
        throw "auto-created review case missing expected rules reason code"
    }

    Write-Host "[auto-review-smoke] complete"
}
finally {
    Stop-Job -Job $acJob -ErrorAction SilentlyContinue
    Stop-Job -Job $apiJob -ErrorAction SilentlyContinue
    Remove-Job -Job $acJob, $apiJob -ErrorAction SilentlyContinue
}
