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
    dotnet run --project src/ControlPlane.Api --urls $u
} -ArgumentList $repo, $Backend

try {
    Start-Sleep -Seconds 8

    Write-Host "[retention-smoke] trigger cleanup..."
    dotnet run --project src/Reviewer.Console -- --backend $Backend --internal-api-key $internalKey run-cleanup
    if ($LASTEXITCODE -ne 0) {
        throw "run-cleanup command failed with exit code $LASTEXITCODE"
    }

    Write-Host "[retention-smoke] read cleanup status..."
    $statusJson = dotnet run --project src/Reviewer.Console -- --backend $Backend --internal-api-key $internalKey cleanup-status
    if ($LASTEXITCODE -ne 0) {
        throw "cleanup-status command failed with exit code $LASTEXITCODE"
    }

    $status = $statusJson | ConvertFrom-Json
    if ($null -eq $status.lastResult) {
        throw "cleanup-status did not return lastResult"
    }

    Write-Host "[retention-smoke] complete"
}
finally {
    Stop-Job -Job $apiJob -ErrorAction SilentlyContinue
    Remove-Job -Job $apiJob -ErrorAction SilentlyContinue
}
