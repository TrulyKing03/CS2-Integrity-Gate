# detection-tuning

Offline threshold calibration utilities for detection channels.

## ThresholdTuner.Console

`ThresholdTuner.Console` reads `suspicion_scores` from the control-plane SQLite database and produces a threshold report JSON with:

- per-channel score distribution (`p50`, `p90`, `p95`, `p99`, min/max/avg),
- recommended review and auto-action thresholds,
- account outlier summary for triage.

### Run

```powershell
dotnet run --project analytics/detection-tuning/ThresholdTuner.Console -- --db src/ControlPlane.Api/data/controlplane.db
```

Optional filters:

```powershell
dotnet run --project analytics/detection-tuning/ThresholdTuner.Console -- --db src/ControlPlane.Api/data/controlplane.db --min-confidence 0.70 --min-samples 30 --channels rules,aim
```

Output default:

- `analytics/detection-tuning/output/threshold-report-<utcstamp>.json`

Helper wrapper script:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/run-threshold-tuner.ps1
```
