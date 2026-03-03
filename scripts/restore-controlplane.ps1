param(
    [Parameter(Mandatory = $true)]
    [string]$BackupPath,
    [string]$TargetDbPath = "src/ControlPlane.Api/data/controlplane.db",
    [string]$EvidencePath = "src/ControlPlane.Api/evidence",
    [switch]$RestoreEvidence,
    [switch]$Force,
    [switch]$SkipHashCheck
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$backupAbsolute = if ([System.IO.Path]::IsPathRooted($BackupPath)) { $BackupPath } else { Join-Path $repo $BackupPath }
if (-not (Test-Path $backupAbsolute)) {
    throw "Backup path not found: $backupAbsolute"
}

$dbBackupPath = Join-Path $backupAbsolute "controlplane.db"
if (-not (Test-Path $dbBackupPath)) {
    throw "Backup database file missing: $dbBackupPath"
}

$manifestPath = Join-Path $backupAbsolute "manifest.json"
$manifest = $null
if (Test-Path $manifestPath) {
    $manifest = Get-Content $manifestPath | ConvertFrom-Json
}

if (-not $SkipHashCheck -and $manifest -ne $null -and $manifest.dbSha256) {
    $actualDbHash = (Get-FileHash -Path $dbBackupPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualDbHash -ne $manifest.dbSha256.ToString().ToLowerInvariant()) {
        throw "Backup DB hash mismatch. Expected $($manifest.dbSha256), got $actualDbHash."
    }
}

$targetDbAbsolute = Join-Path $repo $TargetDbPath
$targetDbDir = Split-Path -Parent $targetDbAbsolute
New-Item -ItemType Directory -Path $targetDbDir -Force | Out-Null

if (Test-Path $targetDbAbsolute) {
    if (-not $Force) {
        throw "Target DB exists: $targetDbAbsolute. Use -Force to overwrite."
    }

    $existingBackup = "$targetDbAbsolute.pre-restore-" + (Get-Date -Format "yyyyMMdd-HHmmss")
    Move-Item -Path $targetDbAbsolute -Destination $existingBackup -Force
    Write-Host "Existing DB moved to: $existingBackup"
}

Copy-Item -Path $dbBackupPath -Destination $targetDbAbsolute -Force
Write-Host "Database restored: $targetDbAbsolute"

if ($RestoreEvidence) {
    $evidenceArchivePath = Join-Path $backupAbsolute "evidence.zip"
    if (-not (Test-Path $evidenceArchivePath)) {
        Write-Host "Evidence archive not present in backup; skipping evidence restore."
        exit 0
    }

    if (-not $SkipHashCheck -and $manifest -ne $null -and $manifest.evidenceSha256) {
        $actualEvidenceHash = (Get-FileHash -Path $evidenceArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualEvidenceHash -ne $manifest.evidenceSha256.ToString().ToLowerInvariant()) {
            throw "Backup evidence hash mismatch. Expected $($manifest.evidenceSha256), got $actualEvidenceHash."
        }
    }

    $evidenceAbsolute = Join-Path $repo $EvidencePath
    if (Test-Path $evidenceAbsolute) {
        if (-not $Force) {
            throw "Evidence path exists: $evidenceAbsolute. Use -Force to overwrite."
        }

        Remove-Item -Recurse -Force $evidenceAbsolute
    }

    New-Item -ItemType Directory -Path $evidenceAbsolute -Force | Out-Null
    Expand-Archive -Path $evidenceArchivePath -DestinationPath $evidenceAbsolute -Force
    Write-Host "Evidence restored: $evidenceAbsolute"
}
