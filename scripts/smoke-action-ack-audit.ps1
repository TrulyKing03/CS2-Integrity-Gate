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
    $username = "ackaudit_$runId"

    Write-Host "[action-ack-smoke] launcher..."
    dotnet run --project src/Launcher.App -- --backend $Backend --username $username --password "local_password" --token-wait-sec 180 --keep-runtime
    if ($LASTEXITCODE -ne 0) {
        throw "launcher failed with exit code $LASTEXITCODE"
    }

    $session = Get-Content $sessionPath | ConvertFrom-Json
    $token = Get-Content $tokenPath | ConvertFrom-Json

    Write-Host "[action-ack-smoke] server bridge simulated cheat..."
    dotnet run --project tools/simulators/ServerBridge.Agent -- --backend $Backend --server-api-key $serverKey --match $session.matchSessionId --server $session.serverId --account $session.accountId --steam $session.steamId --token $token.joinToken --simulate-cheat --runtime-sec 6
    if ($LASTEXITCODE -ne 0) {
        throw "server bridge failed with exit code $LASTEXITCODE"
    }

    Write-Host "[action-ack-smoke] query action ack audit..."
    $acksJson = dotnet run --project src/Reviewer.Console -- --backend $Backend --internal-api-key $internalKey list-action-acks --match $session.matchSessionId --account $session.accountId --limit 20
    if ($LASTEXITCODE -ne 0) {
        throw "list-action-acks failed with exit code $LASTEXITCODE"
    }

    $acks = $acksJson | ConvertFrom-Json
    if ($null -eq $acks -or $acks.Count -lt 1) {
        throw "expected at least one enforcement action ack"
    }

    $valid = $false
    foreach ($ack in $acks) {
        if (-not [string]::IsNullOrWhiteSpace($ack.actionId) -and -not [string]::IsNullOrWhiteSpace($ack.result)) {
            $valid = $true
            break
        }
    }

    if (-not $valid) {
        throw "ack list returned rows without expected action/result fields"
    }

    Write-Host "[action-ack-smoke] complete"
}
finally {
    Stop-Job -Job $acJob -ErrorAction SilentlyContinue
    Stop-Job -Job $apiJob -ErrorAction SilentlyContinue
    Remove-Job -Job $acJob, $apiJob -ErrorAction SilentlyContinue
}
