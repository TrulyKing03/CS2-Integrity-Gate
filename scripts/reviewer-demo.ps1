$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$serverKey = if ($env:CS2IG_SERVER_API_KEY) { $env:CS2IG_SERVER_API_KEY } else { "dev-server-api-key" }
$internalKey = if ($env:CS2IG_INTERNAL_API_KEY) { $env:CS2IG_INTERNAL_API_KEY } else { "dev-internal-api-key" }
$runId = [Guid]::NewGuid().ToString("N").Substring(0, 10)
$accountId = "acc_demo_$runId"
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

    Write-Host "[demo] launcher"
    dotnet run --project src/Launcher.App -- --backend http://localhost:5042 --account $accountId --steam $steamId --keep-runtime
    if ($LASTEXITCODE -ne 0) {
        throw "Launcher failed with exit code $LASTEXITCODE"
    }

    $session = Get-Content $sessionPath | ConvertFrom-Json
    $token = Get-Content $tokenPath | ConvertFrom-Json

    Write-Host "[demo] server simulator via plugin runtime"
    dotnet run --project tools/simulators/ServerBridge.Agent -- --backend http://localhost:5042 --server-api-key $serverKey --match $session.matchSessionId --server $session.serverId --account $session.accountId --steam $session.steamId --token $token.joinToken --simulate-cheat --runtime-sec 8
    if ($LASTEXITCODE -ne 0) {
        throw "Server simulator failed with exit code $LASTEXITCODE"
    }

    Write-Host "[demo] list evidence"
    $evidenceJson = dotnet run --project src/Reviewer.Console -- --backend http://localhost:5042 --internal-api-key $internalKey list-evidence --match $session.matchSessionId --account $session.accountId
    if ($LASTEXITCODE -ne 0) {
        throw "Reviewer.Console list-evidence failed with exit code $LASTEXITCODE"
    }
    $evidence = $evidenceJson | ConvertFrom-Json
    if (-not $evidence -or $evidence.Count -eq 0) {
        throw "No evidence generated for demo flow."
    }

    Write-Host "[demo] system metrics"
    dotnet run --project src/Reviewer.Console -- --backend http://localhost:5042 --internal-api-key $internalKey system-metrics
    if ($LASTEXITCODE -ne 0) {
        throw "Reviewer.Console system-metrics failed with exit code $LASTEXITCODE"
    }

    $evidenceId = $evidence[0].evidenceId
    Write-Host "[demo] create case for evidence $evidenceId"
    $caseJson = dotnet run --project src/Reviewer.Console -- --backend http://localhost:5042 --internal-api-key $internalKey create-case --evidence $evidenceId --match $session.matchSessionId --account $session.accountId --reason rules_impossible_state --priority high --by reviewer_demo
    if ($LASTEXITCODE -ne 0) {
        throw "Reviewer.Console create-case failed with exit code $LASTEXITCODE"
    }
    $case = $caseJson | ConvertFrom-Json

    Write-Host "[demo] update case to in_review"
    dotnet run --project src/Reviewer.Console -- --backend http://localhost:5042 --internal-api-key $internalKey update-case --case $case.caseId --status in_review --reviewer reviewer_demo --notes "triage started"
    if ($LASTEXITCODE -ne 0) {
        throw "Reviewer.Console update-case failed with exit code $LASTEXITCODE"
    }

    Write-Host "[demo] create temporary ban"
    $banJson = dotnet run --project src/Reviewer.Console -- --backend http://localhost:5042 --internal-api-key $internalKey create-ban --account $session.accountId --scope queue --reason confirmed_cheat --evidence $evidenceId --duration-hours 24 --by reviewer_demo
    if ($LASTEXITCODE -ne 0) {
        throw "Reviewer.Console create-ban failed with exit code $LASTEXITCODE"
    }
    $ban = $banJson | ConvertFrom-Json

    Write-Host "[demo] list active bans for account"
    dotnet run --project src/Reviewer.Console -- --backend http://localhost:5042 --internal-api-key $internalKey list-bans --account $session.accountId --status active
    if ($LASTEXITCODE -ne 0) {
        throw "Reviewer.Console list-bans failed with exit code $LASTEXITCODE"
    }

    Write-Host "[demo] create appeal"
    $appealJson = dotnet run --project src/Reviewer.Console -- --backend http://localhost:5042 --internal-api-key $internalKey create-appeal --ban $ban.banId --account $session.accountId --notes "request review"
    if ($LASTEXITCODE -ne 0) {
        throw "Reviewer.Console create-appeal failed with exit code $LASTEXITCODE"
    }
    $appeal = $appealJson | ConvertFrom-Json

    Write-Host "[demo] resolve appeal upheld"
    dotnet run --project src/Reviewer.Console -- --backend http://localhost:5042 --internal-api-key $internalKey resolve-appeal --appeal $appeal.appealId --status upheld --reviewer reviewer_demo --notes "evidence sufficient"
    if ($LASTEXITCODE -ne 0) {
        throw "Reviewer.Console resolve-appeal failed with exit code $LASTEXITCODE"
    }

    Write-Host "[demo] complete"
}
finally {
    Stop-Job -Job $acJob -ErrorAction SilentlyContinue
    Stop-Job -Job $apiJob -ErrorAction SilentlyContinue
    Remove-Job -Job $acJob, $apiJob -ErrorAction SilentlyContinue
}
