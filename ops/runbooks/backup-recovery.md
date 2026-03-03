# Backup and Recovery

This runbook covers local and staging backup/restore for:

- control-plane SQLite database (`src/ControlPlane.Api/data/controlplane.db`)
- evidence artifacts (`src/ControlPlane.Api/evidence`)

## Create Backup

Database only:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/backup-controlplane.ps1
```

Database plus evidence:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/backup-controlplane.ps1 -IncludeEvidence
```

Backup output path:

- `ops/backups/controlplane-<timestamp>/`
- includes `controlplane.db`, `manifest.json`, and optional `evidence.zip`

Retention:

- default cleanup keeps the latest 14 days
- override with `-KeepDays <n>`

## Restore Backup

Restore database only:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/restore-controlplane.ps1 -BackupPath ops/backups/controlplane-20260303-000000 -Force
```

Restore database + evidence:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/restore-controlplane.ps1 -BackupPath ops/backups/controlplane-20260303-000000 -RestoreEvidence -Force
```

## Integrity Verification

Restore script verifies SHA-256 hashes from `manifest.json` by default.

If you need to bypass hash checks for emergency manual recovery:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/restore-controlplane.ps1 -BackupPath <path> -Force -SkipHashCheck
```
