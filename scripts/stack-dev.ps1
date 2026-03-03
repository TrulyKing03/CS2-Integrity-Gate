param(
    [ValidateSet("start", "stop", "status", "restart")]
    [string]$Action = "status",
    [string]$Backend = "http://localhost:5042",
    [string]$RuntimeDir = "runtime",
    [switch]$IncludeGateway
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$runtimeAbsolute = if ([System.IO.Path]::IsPathRooted($RuntimeDir)) { $RuntimeDir } else { Join-Path $repo $RuntimeDir }
$pidsDir = Join-Path $runtimeAbsolute "pids"
$logsDir = Join-Path $runtimeAbsolute "logs"
$apiPidFile = Join-Path $pidsDir "controlplane.pid"
$acPidFile = Join-Path $pidsDir "acclient.pid"
$gatewayPidFile = Join-Path $pidsDir "plugingateway.pid"
$apiLogFile = Join-Path $logsDir "controlplane.log"
$acLogFile = Join-Path $logsDir "acclient.log"
$gatewayLogFile = Join-Path $logsDir "plugingateway.log"

function Ensure-Dirs {
    New-Item -ItemType Directory -Path $runtimeAbsolute -Force | Out-Null
    New-Item -ItemType Directory -Path $pidsDir -Force | Out-Null
    New-Item -ItemType Directory -Path $logsDir -Force | Out-Null
}

function Get-ManagedProcess {
    param([string]$PidFile)
    if (-not (Test-Path $PidFile)) {
        return $null
    }

    $pidRaw = Get-Content $PidFile -ErrorAction SilentlyContinue | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($pidRaw)) {
        Remove-Item $PidFile -ErrorAction SilentlyContinue
        return $null
    }

    $procId = 0
    if (-not [int]::TryParse($pidRaw, [ref]$procId)) {
        Remove-Item $PidFile -ErrorAction SilentlyContinue
        return $null
    }

    $proc = Get-Process -Id $procId -ErrorAction SilentlyContinue
    if ($null -eq $proc) {
        Remove-Item $PidFile -ErrorAction SilentlyContinue
        return $null
    }

    return $proc
}

function Start-ServiceProcess {
    param(
        [string]$Name,
        [string]$PidFile,
        [string]$LogFile,
        [string[]]$ArgumentList
    )

    $existing = Get-ManagedProcess -PidFile $PidFile
    if ($null -ne $existing) {
        Write-Host "[stack] $Name already running (pid=$($existing.Id))"
        return
    }

    $errLogFile = "$LogFile.err"
    if (Test-Path $LogFile) {
        Copy-Item $LogFile "$LogFile.prev" -Force
        Remove-Item $LogFile -ErrorAction SilentlyContinue
    }
    if (Test-Path $errLogFile) {
        Copy-Item $errLogFile "$errLogFile.prev" -Force
        Remove-Item $errLogFile -ErrorAction SilentlyContinue
    }

    $proc = Start-Process -FilePath "dotnet" -ArgumentList $ArgumentList -WorkingDirectory $repo -PassThru -NoNewWindow -RedirectStandardOutput $LogFile -RedirectStandardError $errLogFile
    Set-Content -Path $PidFile -Value $proc.Id
    Write-Host "[stack] started $Name (pid=$($proc.Id), log=$LogFile, err=$errLogFile)"
}

function Stop-ServiceProcess {
    param(
        [string]$Name,
        [string]$PidFile
    )

    $proc = Get-ManagedProcess -PidFile $PidFile
    if ($null -eq $proc) {
        Write-Host "[stack] $Name not running"
        return
    }

    try {
        Stop-Process -Id $proc.Id -ErrorAction Stop
        Write-Host "[stack] stopped $Name (pid=$($proc.Id))"
    }
    finally {
        Remove-Item $PidFile -ErrorAction SilentlyContinue
    }
}

function Show-Status {
    param(
        [string]$Name,
        [string]$PidFile,
        [string]$LogFile
    )

    $proc = Get-ManagedProcess -PidFile $PidFile
    if ($null -eq $proc) {
        Write-Host "[stack] ${Name}: stopped"
        return
    }

    Write-Host "[stack] ${Name}: running pid=$($proc.Id) log=$LogFile"
}

Ensure-Dirs

switch ($Action) {
    "start" {
        Start-ServiceProcess -Name "controlplane" -PidFile $apiPidFile -LogFile $apiLogFile -ArgumentList @("run", "--project", "src/ControlPlane.Api", "--urls", $Backend)
        Start-ServiceProcess -Name "acclient" -PidFile $acPidFile -LogFile $acLogFile -ArgumentList @("run", "--project", "src/AcClient.Service")
        if ($IncludeGateway) {
            Start-ServiceProcess -Name "plugingateway" -PidFile $gatewayPidFile -LogFile $gatewayLogFile -ArgumentList @("run", "--project", "tools/adapters/PluginBridge.Gateway", "--", "Gateway:BackendBaseUrl=$Backend")
        }
        break
    }
    "stop" {
        if ($IncludeGateway) {
            Stop-ServiceProcess -Name "plugingateway" -PidFile $gatewayPidFile
        }
        Stop-ServiceProcess -Name "acclient" -PidFile $acPidFile
        Stop-ServiceProcess -Name "controlplane" -PidFile $apiPidFile
        break
    }
    "status" {
        Show-Status -Name "controlplane" -PidFile $apiPidFile -LogFile $apiLogFile
        Show-Status -Name "acclient" -PidFile $acPidFile -LogFile $acLogFile
        if ($IncludeGateway) {
            Show-Status -Name "plugingateway" -PidFile $gatewayPidFile -LogFile $gatewayLogFile
        }
        break
    }
    "restart" {
        & $PSCommandPath -Action stop -Backend $Backend -RuntimeDir $RuntimeDir -IncludeGateway:$IncludeGateway
        Start-Sleep -Seconds 1
        & $PSCommandPath -Action start -Backend $Backend -RuntimeDir $RuntimeDir -IncludeGateway:$IncludeGateway
        break
    }
}
