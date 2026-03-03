param(
    [string]$Backend = "http://localhost:5042"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$internalKey = if ($env:CS2IG_INTERNAL_API_KEY) { $env:CS2IG_INTERNAL_API_KEY } else { "dev-internal-api-key" }
$runId = [Guid]::NewGuid().ToString("N").Substring(0, 10)
$accountId = "acc_ban_$runId"
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

    Write-Host "[smoke-ban] launcher baseline (should succeed)"
    dotnet run --project src/Launcher.App -- --backend $Backend --account $accountId --steam $steamId --keep-runtime
    if ($LASTEXITCODE -ne 0) {
        throw "Baseline launcher run failed."
    }

    Write-Host "[smoke-ban] create ban"
    $banJson = dotnet run --project src/Reviewer.Console -- --backend $Backend --internal-api-key $internalKey create-ban --account $accountId --scope queue --reason smoke_ban --duration-hours 24 --by smoke_ban
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to create ban."
    }
    $ban = $banJson | ConvertFrom-Json

    Write-Host "[smoke-ban] launcher while banned (should fail with account_banned)"
    $blockedOut = Join-Path $env:TEMP ("cs2ig-blocked-out-" + [Guid]::NewGuid().ToString("N") + ".log")
    $blockedErr = Join-Path $env:TEMP ("cs2ig-blocked-err-" + [Guid]::NewGuid().ToString("N") + ".log")
    $blockedProcess = Start-Process -FilePath "dotnet" -ArgumentList @(
        "run", "--project", "src/Launcher.App", "--",
        "--backend", $Backend,
        "--account", $accountId,
        "--steam", $steamId,
        "--keep-runtime"
    ) -NoNewWindow -Wait -PassThru -RedirectStandardOutput $blockedOut -RedirectStandardError $blockedErr
    $blockedExit = $blockedProcess.ExitCode
    $blockedText = ((Get-Content $blockedOut -Raw -ErrorAction SilentlyContinue) + "`n" + (Get-Content $blockedErr -Raw -ErrorAction SilentlyContinue))
    Remove-Item $blockedOut, $blockedErr -ErrorAction SilentlyContinue
    if ($blockedExit -eq 0) {
        throw "Launcher unexpectedly succeeded while account was banned."
    }
    if ($blockedText -notmatch "account_banned") {
        throw "Expected account_banned error while banned. Output: $blockedText"
    }

    Write-Host "[smoke-ban] create appeal and resolve overturned"
    $appealJson = dotnet run --project src/Reviewer.Console -- --backend $Backend --internal-api-key $internalKey create-appeal --ban $ban.banId --account $accountId --notes "smoke appeal"
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to create appeal."
    }
    $appeal = $appealJson | ConvertFrom-Json

    dotnet run --project src/Reviewer.Console -- --backend $Backend --internal-api-key $internalKey resolve-appeal --appeal $appeal.appealId --status overturned --reviewer smoke_ban --notes "smoke overturned"
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to resolve appeal as overturned."
    }

    Write-Host "[smoke-ban] launcher after overturn (should succeed)"
    dotnet run --project src/Launcher.App -- --backend $Backend --account $accountId --steam $steamId --keep-runtime
    if ($LASTEXITCODE -ne 0) {
        throw "Launcher failed after overturned appeal."
    }

    Write-Host "[smoke-ban] complete"
}
finally {
    Stop-Job -Job $acJob -ErrorAction SilentlyContinue
    Stop-Job -Job $apiJob -ErrorAction SilentlyContinue
    Remove-Job -Job $acJob, $apiJob -ErrorAction SilentlyContinue
}
