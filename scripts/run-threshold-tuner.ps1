param(
    [string]$DbPath = "src/ControlPlane.Api/data/controlplane.db",
    [string]$OutPath = "",
    [double]$MinConfidence = 0.60,
    [int]$MinSamples = 20,
    [string]$Channels = ""
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$args = @(
    "--db", $DbPath,
    "--min-confidence", $MinConfidence.ToString([System.Globalization.CultureInfo]::InvariantCulture),
    "--min-samples", $MinSamples
)

if (-not [string]::IsNullOrWhiteSpace($OutPath)) {
    $args += @("--out", $OutPath)
}

if (-not [string]::IsNullOrWhiteSpace($Channels)) {
    $args += @("--channels", $Channels)
}

dotnet run --project analytics/detection-tuning/ThresholdTuner.Console -- @args
exit $LASTEXITCODE
