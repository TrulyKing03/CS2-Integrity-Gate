param(
    [string]$Backend = "http://localhost:5042"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$internalKey = if ($env:CS2IG_INTERNAL_API_KEY) { $env:CS2IG_INTERNAL_API_KEY } else { "dev-internal-api-key" }

$apiJob = Start-Job -ScriptBlock {
    param($r, $u)
    Set-Location $r
    $env:ApiAuth__RequireQueueAccessToken = "true"
    dotnet run --project src/ControlPlane.Api --urls $u
} -ArgumentList $repo, $Backend

function Assert-QueueUnauthorized {
    param(
        [string]$Uri,
        [hashtable]$Headers,
        [string]$Body,
        [string]$Context
    )

    $statusCode = 0
    $failed = $false
    try {
        Invoke-RestMethod -Method Post -Uri $Uri -Headers $Headers -ContentType "application/json" -Body $Body | Out-Null
    }
    catch {
        $failed = $true
        $response = $_.Exception.Response
        if ($null -ne $response) {
            try {
                $statusCode = [int]$response.StatusCode
            }
            catch {
                try {
                    $statusCode = [int]$response.StatusCode.value__
                }
                catch {
                    $statusCode = 0
                }
            }
        }
    }

    if (-not $failed) {
        throw "expected unauthorized queue response for $Context"
    }

    if ($statusCode -ne 401) {
        throw "expected 401 for $Context, got $statusCode"
    }
}

try {
    Start-Sleep -Seconds 8

    $runId = [Guid]::NewGuid().ToString("N").Substring(0, 10)
    $loginBody = @{
        username = "sessionrevoke_$runId"
        password = "local_password"
    } | ConvertTo-Json

    Write-Host "[session-revoke-smoke] login..."
    $login = Invoke-RestMethod -Method Post -Uri "$Backend/v1/auth/login" -ContentType "application/json" -Body $loginBody
    if ($null -eq $login -or [string]::IsNullOrWhiteSpace($login.accessToken)) {
        throw "login did not return access token"
    }

    $queueBody = @{
        accountId = $login.accountId
        steamId = $login.steamId
        queueType = "high_trust"
    } | ConvertTo-Json

    $authHeaders = @{ Authorization = "Bearer $($login.accessToken)" }

    Write-Host "[session-revoke-smoke] queue with active token (should succeed)..."
    $queueOk = Invoke-RestMethod -Method Post -Uri "$Backend/v1/queue/enqueue" -Headers $authHeaders -ContentType "application/json" -Body $queueBody
    if ($null -eq $queueOk -or [string]::IsNullOrWhiteSpace($queueOk.matchSessionId)) {
        throw "queue response missing match session id"
    }

    Write-Host "[session-revoke-smoke] logout token..."
    $logout = Invoke-RestMethod -Method Post -Uri "$Backend/v1/auth/logout" -Headers $authHeaders
    if (-not $logout.revoked) {
        throw "logout did not revoke token"
    }

    Write-Host "[session-revoke-smoke] queue with logged-out token (should be 401)..."
    Assert-QueueUnauthorized -Uri "$Backend/v1/queue/enqueue" -Headers $authHeaders -Body $queueBody -Context "post-logout token"

    Write-Host "[session-revoke-smoke] login second token..."
    $login2 = Invoke-RestMethod -Method Post -Uri "$Backend/v1/auth/login" -ContentType "application/json" -Body $loginBody
    $authHeaders2 = @{ Authorization = "Bearer $($login2.accessToken)" }

    Write-Host "[session-revoke-smoke] internal revoke all account sessions..."
    dotnet run --project src/Reviewer.Console -- --backend $Backend --internal-api-key $internalKey revoke-sessions --account $login.accountId --reason smoke_revoke --by smoke
    if ($LASTEXITCODE -ne 0) {
        throw "revoke-sessions command failed with exit code $LASTEXITCODE"
    }

    Write-Host "[session-revoke-smoke] queue with revoked account token (should be 401)..."
    Assert-QueueUnauthorized -Uri "$Backend/v1/queue/enqueue" -Headers $authHeaders2 -Body $queueBody -Context "post-account session revoke"

    Write-Host "[session-revoke-smoke] complete"
}
finally {
    Stop-Job -Job $apiJob -ErrorAction SilentlyContinue
    Remove-Job -Job $apiJob -ErrorAction SilentlyContinue
}
