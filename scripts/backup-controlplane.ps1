param(
    [string]$SourceDbPath = "src/ControlPlane.Api/data/controlplane.db",
    [string]$EvidencePath = "src/ControlPlane.Api/evidence",
    [string]$BackupRoot = "ops/backups",
    [int]$KeepDays = 14,
    [switch]$IncludeEvidence
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$sourceDbAbsolute = Join-Path $repo $SourceDbPath
if (-not (Test-Path $sourceDbAbsolute)) {
    throw "Database file not found: $sourceDbAbsolute"
}

$backupRootAbsolute = Join-Path $repo $BackupRoot
New-Item -ItemType Directory -Path $backupRootAbsolute -Force | Out-Null

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backupDir = Join-Path $backupRootAbsolute "controlplane-$stamp"
New-Item -ItemType Directory -Path $backupDir -Force | Out-Null

$dbBackupPath = Join-Path $backupDir "controlplane.db"
Copy-Item -Path $sourceDbAbsolute -Destination $dbBackupPath -Force
$dbHash = (Get-FileHash -Path $dbBackupPath -Algorithm SHA256).Hash.ToLowerInvariant()

$evidenceIncluded = $false
$evidenceArchivePath = $null
$evidenceHash = $null
if ($IncludeEvidence) {
    $evidenceAbsolute = Join-Path $repo $EvidencePath
    if (Test-Path $evidenceAbsolute) {
        $evidenceFiles = Get-ChildItem -Path $evidenceAbsolute -File -Recurse
        if ($evidenceFiles.Count -gt 0) {
            $evidenceArchivePath = Join-Path $backupDir "evidence.zip"
            Compress-Archive -Path (Join-Path $evidenceAbsolute "*") -DestinationPath $evidenceArchivePath -CompressionLevel Optimal -Force
            $evidenceHash = (Get-FileHash -Path $evidenceArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
            $evidenceIncluded = $true
        }
    }
}

$manifest = [ordered]@{
    createdAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    sourceDbPath = $SourceDbPath
    dbBackupFile = "controlplane.db"
    dbSha256 = $dbHash
    evidenceIncluded = $evidenceIncluded
    evidenceSourcePath = $EvidencePath
    evidenceArchiveFile = if ($evidenceIncluded) { "evidence.zip" } else { $null }
    evidenceSha256 = $evidenceHash
}

$manifestPath = Join-Path $backupDir "manifest.json"
$manifest | ConvertTo-Json -Depth 5 | Set-Content -Path $manifestPath

if ($KeepDays -gt 0) {
    $cutoff = (Get-Date).ToUniversalTime().AddDays(-$KeepDays)
    Get-ChildItem -Path $backupRootAbsolute -Directory |
        Where-Object { $_.LastWriteTimeUtc -lt $cutoff } |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Backup created: $backupDir"
Write-Host "DB hash: $dbHash"
if ($evidenceIncluded) {
    Write-Host "Evidence archive hash: $evidenceHash"
}
