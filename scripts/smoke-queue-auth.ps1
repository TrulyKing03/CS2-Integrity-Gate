param(
    [string]$Backend = "http://localhost:5042"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

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
    $env:ApiAuth__RequireQueueAccessToken = "true"
    dotnet run --project src/ControlPlane.Api --urls $u
} -ArgumentList $repo, $Backend

$acJob = Start-Job -ScriptBlock {
    param($r)
    Set-Location $r
    dotnet run --project src/AcClient.Service
} -ArgumentList $repo

try {
    Start-Sleep -Seconds 8

    Write-Host "[queue-auth-smoke] verify unauthorized queue attempt..."
    $runId = [Guid]::NewGuid().ToString("N").Substring(0, 10)
    $accountId = "acc_queue_auth_$runId"
    $steamId = "7656119" + (Get-Random -Minimum 10000000000 -Maximum 99999999999)
    dotnet run --project src/Launcher.App -- --backend $Backend --account $accountId --steam $steamId --token-wait-sec 30 --keep-runtime
    if ($LASTEXITCODE -eq 0) {
        throw "Expected launcher queue request to fail without bearer token when RequireQueueAccessToken=true"
    }

    Write-Host "[queue-auth-smoke] verify authorized queue attempt via login token..."
    Remove-Item $sessionPath -ErrorAction SilentlyContinue
    Remove-Item $tokenPath -ErrorAction SilentlyContinue

    $username = "queueauth_$runId"
    dotnet run --project src/Launcher.App -- --backend $Backend --username $username --password "local_password" --token-wait-sec 180 --keep-runtime
    if ($LASTEXITCODE -ne 0) {
        throw "Expected launcher queue request to succeed with login bearer token"
    }

    $session = Get-Content $sessionPath | ConvertFrom-Json
    $token = Get-Content $tokenPath | ConvertFrom-Json

    Write-Host "[queue-auth-smoke] validate join + telemetry path..."
    dotnet run --project tools/simulators/ServerBridge.Agent -- --backend $Backend --server-api-key $serverKey --match $session.matchSessionId --server $session.serverId --account $session.accountId --steam $session.steamId --token $token.joinToken --simulate-cheat --runtime-sec 6
    if ($LASTEXITCODE -ne 0) {
        throw "Server bridge failed with exit code $LASTEXITCODE"
    }

    Write-Host "[queue-auth-smoke] complete"
}
finally {
    Stop-Job -Job $acJob -ErrorAction SilentlyContinue
    Stop-Job -Job $apiJob -ErrorAction SilentlyContinue
    Remove-Job -Job $acJob, $apiJob -ErrorAction SilentlyContinue
}
