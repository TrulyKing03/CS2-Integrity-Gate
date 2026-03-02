$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo
dotnet run --project src/ControlPlane.Api --urls http://localhost:5042
