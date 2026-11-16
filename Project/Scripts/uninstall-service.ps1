[CmdletBinding()]
param(
    [string]$ServiceName = "OneCIntegrationService",
    [string]$InstallDir = "C:\OneCIntegrationService",
    [switch]$RemoveFiles
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
                throw "Файл '$Path' все еще заблокирован после $TimeoutSeconds секунд. Каталог не удален."
            }
            Start-Sleep -Milliseconds 500
        }
    }
}

$Service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($Service) {
    Write-Host "Остановка службы $ServiceName..."
    $Service.Refresh()
    if ($Service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        Stop-Service -Name $ServiceName -Force
        $Service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
    }

    Write-Host "Удаление регистрации службы..."
    & sc.exe delete $ServiceName | Out-Null
    $ScExitCode = $LASTEXITCODE
    if ($ScExitCode -ne 0) {
        throw "sc.exe delete завершился с кодом $ScExitCode. Служба не считается удаленной."
    }

    $Deadline = (Get-Date).AddSeconds(30)
    while (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        if ((Get-Date) -ge $Deadline) {
            throw "Таймаут ожидания фактического удаления службы $ServiceName из диспетчера служб."
        }
        Start-Sleep -Milliseconds 500
    }

    Write-Host "Служба $ServiceName удалена из диспетчера служб." -ForegroundColor Green
} else {
    Write-Warning "Служба $ServiceName не найдена."
}

if ($RemoveFiles -and (Test-Path -LiteralPath $InstallDir -PathType Container)) {
    $TargetExe = Join-Path $InstallDir "OneCIntegrationService.exe"
    if (Test-Path -LiteralPath $TargetExe -PathType Leaf) {
        Wait-ForFileUnlock -Path $TargetExe -TimeoutSeconds 30
    }

    Remove-Item -LiteralPath $InstallDir -Recurse -Force
    Write-Host "Каталог $InstallDir успешно удален." -ForegroundColor Green
}