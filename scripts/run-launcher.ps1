param(
    [string]$Backend = "http://localhost:5042",
    [string]$Account = "acc_local_demo",
    [string]$Steam = "76561190000000001",
    [switch]$SelfValidate
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$args = @(
    "--backend", $Backend,
    "--account", $Account,
    "--steam", $Steam,
    "--keep-runtime"
)

if ($SelfValidate) {
    $args += "--self-validate"
}

dotnet run --project src/Launcher.Cli -- @args
