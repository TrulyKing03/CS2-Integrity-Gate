$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo
$serverKey = if ($env:CS2IG_SERVER_API_KEY) { $env:CS2IG_SERVER_API_KEY } else { "dev-server-api-key" }
$runId = [Guid]::NewGuid().ToString("N").Substring(0, 10)
$accountId = "acc_smoke_$runId"
$steamId = "7656119" + (Get-Random -Minimum 10000000000 -Maximum 99999999999)
$runtimeDir = Join-Path $repo "runtime"
$sessionPath = Join-Path $runtimeDir "session.json"
$tokenPath = Join-Path $runtimeDir "join-token.json"

if (-not (Test-Path $runtimeDir)) {
    New-Item -ItemType Directory -Path $runtimeDir | Out-Null
}
Remove-Item $sessionPath -ErrorAction SilentlyContinue
Remove-Item $tokenPath -ErrorAction SilentlyContinue

$apiJob = Start-Job -ScriptBlock {
    param($r)
    Set-Location $r
    dotnet run --project src/ControlPlane.Api --urls http://localhost:5042
} -ArgumentList $repo

$acJob = Start-Job -ScriptBlock {
    param($r)
    Set-Location $r
    dotnet run --project src/AcClient.Service
} -ArgumentList $repo

try {
    Start-Sleep -Seconds 8

    Write-Host "[smoke] running launcher..."
    dotnet run --project src/Launcher.App -- --backend http://localhost:5042 --account $accountId --steam $steamId --keep-runtime
    if ($LASTEXITCODE -ne 0) {
        throw "Launcher failed with exit code $LASTEXITCODE"
    }

    $session = Get-Content $sessionPath | ConvertFrom-Json
    $token = Get-Content $tokenPath | ConvertFrom-Json

    Write-Host "[smoke] running server bridge (simulated cheat telemetry)..."
    dotnet run --project tools/simulators/ServerBridge.Agent -- --backend http://localhost:5042 --server-api-key $serverKey --match $session.matchSessionId --server $session.serverId --account $session.accountId --steam $session.steamId --token $token.joinToken --simulate-cheat --runtime-sec 8
    if ($LASTEXITCODE -ne 0) {
        throw "Server bridge failed with exit code $LASTEXITCODE"
    }

    Write-Host "[smoke] complete"
}
finally {
    Stop-Job -Job $acJob -ErrorAction SilentlyContinue
    Stop-Job -Job $apiJob -ErrorAction SilentlyContinue
    Remove-Job -Job $acJob, $apiJob -ErrorAction SilentlyContinue
}
