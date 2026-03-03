param(
    [string]$Backend = "http://localhost:5042"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo
$serverKey = if ($env:CS2IG_SERVER_API_KEY) { $env:CS2IG_SERVER_API_KEY } else { "dev-server-api-key" }
$requiredPolicyHash = "sha256:enforced_policy_hash_smoke"

$runtimeDir = Join-Path $repo "runtime"
$sessionPath = Join-Path $runtimeDir "session.json"
$tokenPath = Join-Path $runtimeDir "join-token.json"
if (-not (Test-Path $runtimeDir)) {
    New-Item -ItemType Directory -Path $runtimeDir | Out-Null
}

Remove-Item $sessionPath -ErrorAction SilentlyContinue
Remove-Item $tokenPath -ErrorAction SilentlyContinue

$apiJob = Start-Job -ScriptBlock {
    param($r, $u, $requiredHash)
    Set-Location $r
    $env:AcPolicy__RequiredPolicyHashes__0 = $requiredHash
    dotnet run --project src/ControlPlane.Api --urls $u
} -ArgumentList $repo, $Backend, $requiredPolicyHash

try {
    Start-Sleep -Seconds 8

    Write-Host "[policy-hash-smoke] phase 1: mismatch should fail..."
    $acJob = Start-Job -ScriptBlock {
        param($r)
        Set-Location $r
        dotnet run --project src/AcClient.Service
    } -ArgumentList $repo

    try {
        $runId1 = [Guid]::NewGuid().ToString("N").Substring(0, 10)
        $account1 = "acc_policy_fail_$runId1"
        $steam1 = "7656119" + (Get-Random -Minimum 10000000000 -Maximum 99999999999)
        $previousPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = "Continue"
            dotnet run --project src/Launcher.App -- --backend $Backend --account $account1 --steam $steam1 --token-wait-sec 30 --keep-runtime *> $null
        }
        finally {
            $ErrorActionPreference = $previousPreference
        }

        if ($LASTEXITCODE -eq 0) {
            throw "Expected launcher to fail when AC policy hash is not allowed"
        }
        Write-Host "[policy-hash-smoke] mismatch rejected as expected"
    }
    finally {
        Stop-Job -Job $acJob -ErrorAction SilentlyContinue
        Remove-Job -Job $acJob -ErrorAction SilentlyContinue
    }

    Remove-Item $sessionPath -ErrorAction SilentlyContinue
    Remove-Item $tokenPath -ErrorAction SilentlyContinue

    Write-Host "[policy-hash-smoke] phase 2: matching hash should pass..."
    $acJob2 = Start-Job -ScriptBlock {
        param($r, $hash)
        Set-Location $r
        $env:AcClient__PolicyHash = $hash
        dotnet run --project src/AcClient.Service
    } -ArgumentList $repo, $requiredPolicyHash

    try {
        $runId2 = [Guid]::NewGuid().ToString("N").Substring(0, 10)
        $account2 = "acc_policy_pass_$runId2"
        $steam2 = "7656119" + (Get-Random -Minimum 10000000000 -Maximum 99999999999)
        dotnet run --project src/Launcher.App -- --backend $Backend --account $account2 --steam $steam2 --token-wait-sec 180 --keep-runtime
        if ($LASTEXITCODE -ne 0) {
            throw "Expected launcher to succeed when AC policy hash matches required hash"
        }

        $session = Get-Content $sessionPath | ConvertFrom-Json
        $token = Get-Content $tokenPath | ConvertFrom-Json
        dotnet run --project tools/simulators/ServerBridge.Agent -- --backend $Backend --server-api-key $serverKey --match $session.matchSessionId --server $session.serverId --account $session.accountId --steam $session.steamId --token $token.joinToken --simulate-cheat --runtime-sec 6
        if ($LASTEXITCODE -ne 0) {
            throw "Server bridge failed after successful policy-hash gate"
        }
    }
    finally {
        Stop-Job -Job $acJob2 -ErrorAction SilentlyContinue
        Remove-Job -Job $acJob2 -ErrorAction SilentlyContinue
    }

    Write-Host "[policy-hash-smoke] complete"
}
finally {
    Stop-Job -Job $apiJob -ErrorAction SilentlyContinue
    Remove-Job -Job $apiJob -ErrorAction SilentlyContinue
}
