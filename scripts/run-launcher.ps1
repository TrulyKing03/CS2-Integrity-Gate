param(
    [ValidateSet("play", "doctor", "status", "clear-runtime")]
    [string]$Command = "play",
    [string]$Profile = "",
    [string]$Backend = "http://localhost:5042",
    [string]$Account = "acc_local_demo",
    [string]$Steam = "76561190000000001",
    [switch]$SelfValidate,
    [string]$RuntimeSigningKey = ""
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$args = @(
    $Command
)

if (-not [string]::IsNullOrWhiteSpace($Profile)) {
    $args += @("--profile", $Profile)
}
else {
    $args += @(
        "--backend", $Backend,
        "--account", $Account,
        "--steam", $Steam,
        "--keep-runtime"
    )

    if ($SelfValidate) {
        $args += "--self-validate"
    }

    if (-not [string]::IsNullOrWhiteSpace($RuntimeSigningKey)) {
        $args += @("--runtime-signing-key", $RuntimeSigningKey)
    }
}

dotnet run --project src/Launcher.App -- @args
