[CmdletBinding()]
param(
    [string]$ServiceName = "OneCIntegrationService",
    [string]$InstallDir = "C:\OneCIntegrationService",
    [string]$SourceDir = ".\publish"
)

$ErrorActionPreference = "Stop"

function Wait-ForFileUnlock {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [int]$TimeoutSeconds = 30
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ($true) {
        try {
            $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
            $stream.Dispose()
            return
        } catch {
            if ((Get-Date) -ge $deadline) {
                throw "Файл '$Path' остается заблокированным по истечении $TimeoutSeconds секунд. Обновление прекращено."
            }
            Start-Sleep -Milliseconds 500
        }
    }
}

$Service = Get-Service -Name $ServiceName -ErrorAction Stop
$TargetExe = Join-Path $InstallDir "OneCIntegrationService.exe"
$BackupExe = Join-Path $InstallDir "OneCIntegrationService.exe.bak"
$NewExe = Join-Path $SourceDir "OneCIntegrationService.exe"

if (-not (Test-Path -LiteralPath $TargetExe -PathType Leaf)) {
    throw "Не найден установленный EXE: $TargetExe"
}
if (-not (Test-Path -LiteralPath $NewExe -PathType Leaf)) {
    throw "Не найден новый EXE: $NewExe"
}

Write-Host "Остановка службы $ServiceName..."
$Service.Refresh()
if ($Service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
    Stop-Service -Name $ServiceName -Force
    $Service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
}

$BackupCreated = $false
try {
    if (Test-Path -LiteralPath $BackupExe) {
        Remove-Item -LiteralPath $BackupExe -Force
    }

    Wait-ForFileUnlock -Path $TargetExe -TimeoutSeconds 30
    Copy-Item -LiteralPath $TargetExe -Destination $BackupExe -Force
    $BackupCreated = $true

    Write-Host "Замена исполняемого файла. appsettings.json сохраняется..."
    Copy-Item -LiteralPath $NewExe -Destination $TargetExe -Force

    Start-Service -Name $ServiceName
    $Service = Get-Service -Name $ServiceName
    $Service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(30))

    Remove-Item -LiteralPath $BackupExe -Force
    Write-Host "Обновление службы $ServiceName успешно завершено." -ForegroundColor Green
} catch {
    $UpdateError = $_
    Write-Warning "Не удалось обновить/запустить новую версию: $UpdateError. Выполняется восстановление."

    try {
        $Service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($Service -and $Service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
            Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
            $Service = Get-Service -Name $ServiceName
            $Service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
        }

        if ($BackupCreated -and (Test-Path -LiteralPath $BackupExe -PathType Leaf)) {
            Wait-ForFileUnlock -Path $TargetExe -TimeoutSeconds 30
            Copy-Item -LiteralPath $BackupExe -Destination $TargetExe -Force
            Remove-Item -LiteralPath $BackupExe -Force
            Write-Host "Предыдущая версия EXE восстановлена." -ForegroundColor Yellow
        }

        Start-Service -Name $ServiceName
        $Service = Get-Service -Name $ServiceName
        $Service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(30))
        Write-Host "Служба запущена после восстановления." -ForegroundColor Yellow
    } catch {
        throw "Обновление не удалось: $UpdateError. Восстановление также завершилось ошибкой: $_. Служба требует ручного восстановления."
    }

    throw $UpdateError
}