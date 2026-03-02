$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

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
    dotnet run --project src/Launcher.Cli -- --backend http://localhost:5042 --account acc_local_demo --steam 76561190000000001 --keep-runtime

    $session = Get-Content (Join-Path $repo "runtime/session.json") | ConvertFrom-Json
    $token = Get-Content (Join-Path $repo "runtime/join-token.json") | ConvertFrom-Json

    Write-Host "[smoke] running server bridge (simulated cheat telemetry)..."
    dotnet run --project src/ServerBridge.Agent -- --backend http://localhost:5042 --match $session.matchSessionId --server $session.serverId --account $session.accountId --steam $session.steamId --token $token.joinToken --simulate-cheat --runtime-sec 8

    Write-Host "[smoke] complete"
}
finally {
    Stop-Job -Job $acJob -ErrorAction SilentlyContinue
    Stop-Job -Job $apiJob -ErrorAction SilentlyContinue
    Remove-Job -Job $acJob, $apiJob -ErrorAction SilentlyContinue
}
