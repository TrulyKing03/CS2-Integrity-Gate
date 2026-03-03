param(
    [string]$Backend = "http://localhost:5042",
    [string]$GatewayUrl = "http://localhost:5055",
    [string]$BridgeApiKey = "dev-bridge-api-key"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$serverKey = if ($env:CS2IG_SERVER_API_KEY) { $env:CS2IG_SERVER_API_KEY } else { "dev-server-api-key" }
$runId = [Guid]::NewGuid().ToString("N").Substring(0, 10)
$accountId = "acc_gateway_$runId"
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

$gatewayJob = Start-Job -ScriptBlock {
    param($r, $backendUrl, $listenUrl, $bridgeKey, $serverApiKey)
    Set-Location $r
    $env:Gateway__BackendBaseUrl = $backendUrl
    $env:Gateway__ListenUrl = $listenUrl
    $env:Gateway__BridgeApiKey = $bridgeKey
    $env:Gateway__ServerApiKey = $serverApiKey
    dotnet run --project tools/adapters/PluginBridge.Gateway
} -ArgumentList $repo, $Backend, $GatewayUrl, $BridgeApiKey, $serverKey

try {
    Start-Sleep -Seconds 10

    Write-Host "[gateway-smoke] running launcher..."
    dotnet run --project src/Launcher.App -- --backend $Backend --account $accountId --steam $steamId --token-wait-sec 180 --keep-runtime
    if ($LASTEXITCODE -ne 0) {
        throw "Launcher failed with exit code $LASTEXITCODE"
    }

    $session = Get-Content $sessionPath | ConvertFrom-Json
    $token = Get-Content $tokenPath | ConvertFrom-Json
    $headers = @{ "X-Bridge-Api-Key" = $BridgeApiKey }

    Write-Host "[gateway-smoke] connect attempt via gateway..."
    $connectBody = @{
        matchSessionId = $session.matchSessionId
        serverId = $session.serverId
        accountId = $session.accountId
        steamId = $session.steamId
        joinToken = $token.joinToken
    } | ConvertTo-Json
    $connectResult = Invoke-RestMethod -Method Post -Uri "$GatewayUrl/v1/plugin/connect-attempt" -Headers $headers -ContentType "application/json" -Body $connectBody
    if (-not $connectResult.allow) {
        throw "Gateway denied connect: $($connectResult.reason)"
    }

    $sessionBody = @{
        matchSessionId = $session.matchSessionId
        accountId = $session.accountId
        steamId = $session.steamId
    } | ConvertTo-Json
    Invoke-RestMethod -Method Post -Uri "$GatewayUrl/v1/plugin/connected" -Headers $headers -ContentType "application/json" -Body $sessionBody | Out-Null

    Write-Host "[gateway-smoke] posting telemetry..."
    $ticks = @()
    for ($i = 0; $i -lt 6; $i++) {
        $tickId = 2000 + $i
        $ticks += @{
            matchSessionId = $session.matchSessionId
            tickId = $tickId
            tickUtc = [DateTimeOffset]::UtcNow.ToString("o")
            accountId = $session.accountId
            steamId = $session.steamId
            team = "CT"
            posX = 12.2
            posY = 8.1
            posZ = 1.4
            velX = 500.0
            velY = 0.2
            velZ = -0.1
            yaw = 122.4
            pitch = -0.7
            stance = "standing"
            weaponId = "ak47"
            ammoClip = 30
            isReloading = $false
            pingMs = 34
            lossPct = 0.1
            chokePct = 0.0
        }
    }
    $ticksBody = @{ items = $ticks } | ConvertTo-Json -Depth 6
    Invoke-RestMethod -Method Post -Uri "$GatewayUrl/v1/plugin/ticks" -Headers $headers -ContentType "application/json" -Body $ticksBody | Out-Null

    $los = @(
        @{
            matchSessionId = $session.matchSessionId
            tickId = 2000
            tickUtc = [DateTimeOffset]::UtcNow.ToString("o")
            observerAccountId = $session.accountId
            targetAccountId = "acc_enemy"
            lineOfSight = $false
            audibleProxy = $false
            distanceMeters = 16.5
        }
    )
    $losBody = @{ items = $los } | ConvertTo-Json -Depth 6
    Invoke-RestMethod -Method Post -Uri "$GatewayUrl/v1/plugin/visibility" -Headers $headers -ContentType "application/json" -Body $losBody | Out-Null

    $shots = @()
    for ($i = 0; $i -lt 6; $i++) {
        $tickId = 2001 + $i
        $shots += @{
            matchSessionId = $session.matchSessionId
            tickId = $tickId
            tickUtc = [DateTimeOffset]::UtcNow.ToString("o")
            shooterAccountId = $session.accountId
            shooterSteamId = $session.steamId
            weaponId = "ak47"
            recoilIndex = 0
            yaw = 120.5
            pitch = -1.2
            hitPlayer = $true
            hitAccountId = "acc_enemy"
            hitSteamId = "76561190000000009"
        }
    }
    $shotsBody = @{ items = $shots } | ConvertTo-Json -Depth 6
    Invoke-RestMethod -Method Post -Uri "$GatewayUrl/v1/plugin/shots" -Headers $headers -ContentType "application/json" -Body $shotsBody | Out-Null

    $flushBody = @{ matchSessionId = $session.matchSessionId } | ConvertTo-Json
    Invoke-RestMethod -Method Post -Uri "$GatewayUrl/v1/plugin/flush" -Headers $headers -ContentType "application/json" -Body $flushBody | Out-Null

    Start-Sleep -Seconds 6

    $consumeBody = @{
        matchSessionId = $session.matchSessionId
        accountId = $session.accountId
    } | ConvertTo-Json
    $consumed = Invoke-RestMethod -Method Post -Uri "$GatewayUrl/v1/plugin/host-actions/consume" -Headers $headers -ContentType "application/json" -Body $consumeBody
    $actionCount = @($consumed.actions).Count
    if ($actionCount -lt 1) {
        throw "Expected at least 1 host action from gateway, got $actionCount"
    }

    Invoke-RestMethod -Method Post -Uri "$GatewayUrl/v1/plugin/disconnected" -Headers $headers -ContentType "application/json" -Body $sessionBody | Out-Null
    Write-Host "[gateway-smoke] complete actions=$actionCount"
}
finally {
    Stop-Job -Job $gatewayJob -ErrorAction SilentlyContinue
    Stop-Job -Job $acJob -ErrorAction SilentlyContinue
    Stop-Job -Job $apiJob -ErrorAction SilentlyContinue
    Remove-Job -Job $gatewayJob, $acJob, $apiJob -ErrorAction SilentlyContinue
}
