[CmdletBinding()]
param(
    [string]$ServiceName = "OneCIntegrationService",
    [string]$DisplayName = "1С: Служба мониторинга лог-файлов",
    [string]$InstallDir = "C:\OneCIntegrationService",
    [string]$SourceDir = ".\publish",
    [string[]]$LogDirectories = @("D:\1C Log"),
    [Parameter(Mandatory = $true)]
    [PSCredential]$ServiceAccount
)

$ErrorActionPreference = "Stop"

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Запустите install-service.ps1 из PowerShell от имени администратора."
    }
}

Assert-Administrator

if (-not [Environment]::Is64BitProcess) {
    throw "Скрипт установки должен выполняться из 64-разрядного PowerShell. Служба собрана только для win-x64."
}

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    throw "Служба $ServiceName уже установлена. Для обновления используйте update-service.ps1."
}

$ExeSource = Join-Path $SourceDir "OneCIntegrationService.exe"
$SettingsSource = Join-Path $SourceDir "appsettings.json"
if (-not (Test-Path -LiteralPath $ExeSource -PathType Leaf)) {
    throw "Не найден исполняемый файл: $ExeSource"
}
if (-not (Test-Path -LiteralPath $SettingsSource -PathType Leaf)) {
    throw "Не найден файл конфигурации: $SettingsSource"
}

# Только проверка регистрации. Скрипт не перерегистрирует COMConnector.
$ComClsid = Get-ItemProperty -Path "Registry::HKEY_CLASSES_ROOT\V83.COMConnector\CLSID" -ErrorAction SilentlyContinue
if (-not $ComClsid) {
    Write-Warning "В 64-разрядном реестре не найден V83.COMConnector. Проверьте регистрацию x64 comcntr.dll и соответствие версии платформы 1С."
} else {
    Write-Host "V83.COMConnector обнаружен (CLSID: $($ComClsid.'(default)'))." -ForegroundColor Cyan
}

if (-not [System.Diagnostics.EventLog]::SourceExists($ServiceName)) {
    New-EventLog -LogName Application -Source $ServiceName
    Write-Host "Создан источник Windows Event Log: $ServiceName" -ForegroundColor Cyan
}

New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
Copy-Item -LiteralPath $ExeSource -Destination $InstallDir -Force
Copy-Item -LiteralPath $SettingsSource -Destination $InstallDir -Force

# Каталог службы: FullControl только Administrators и SYSTEM;
# учетная запись службы получает исключительно ReadAndExecute.
$Acl = Get-Acl -LiteralPath $InstallDir
$Acl.SetAccessRuleProtection($true, $false)
foreach ($ExistingRule in @($Acl.Access)) {
    [void]$Acl.RemoveAccessRuleAll($ExistingRule)
}
$AdminRule = [System.Security.AccessControl.FileSystemAccessRule]::new(
    "BUILTIN\Administrators", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")
$SystemRule = [System.Security.AccessControl.FileSystemAccessRule]::new(
    "NT AUTHORITY\SYSTEM", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")
$ServiceRule = [System.Security.AccessControl.FileSystemAccessRule]::new(
    $ServiceAccount.UserName, "ReadAndExecute", "ContainerInherit,ObjectInherit", "None", "Allow")
$Acl.SetAccessRule($AdminRule)
$Acl.SetAccessRule($SystemRule)
$Acl.SetAccessRule($ServiceRule)
Set-Acl -LiteralPath $InstallDir -AclObject $Acl
Write-Host "ACL каталога службы настроен: $($ServiceAccount.UserName) — ReadAndExecute." -ForegroundColor Cyan

$GrantedLogDirectories = 0
foreach ($LogDirectory in $LogDirectories) {
    if ([string]::IsNullOrWhiteSpace($LogDirectory)) {
        continue
    }

    if (-not [System.IO.Path]::IsPathFullyQualified($LogDirectory)) {
        throw "Путь каталога логов должен быть абсолютным: $LogDirectory"
    }

    if (-not (Test-Path -LiteralPath $LogDirectory -PathType Container)) {
        Write-Warning "Каталог логов '$LogDirectory' пока не существует. После его создания выдайте учетной записи $($ServiceAccount.UserName) права ReadAndExecute."
        continue
    }

    $LogAcl = Get-Acl -LiteralPath $LogDirectory
    $LogReadRule = [System.Security.AccessControl.FileSystemAccessRule]::new(
        $ServiceAccount.UserName, "ReadAndExecute", "ContainerInherit,ObjectInherit", "None", "Allow")
    $LogAcl.SetAccessRule($LogReadRule)
    Set-Acl -LiteralPath $LogDirectory -AclObject $LogAcl

    $VerifiedAcl = Get-Acl -LiteralPath $LogDirectory
    $RuleVerified = $false
    foreach ($Rule in $VerifiedAcl.Access) {
        if ($Rule.IdentityReference.Value -eq $ServiceAccount.UserName -and
            (($Rule.FileSystemRights -band [System.Security.AccessControl.FileSystemRights]::ReadAndExecute) -eq
                [System.Security.AccessControl.FileSystemRights]::ReadAndExecute) -and
            $Rule.AccessControlType -eq [System.Security.AccessControl.AccessControlType]::Allow) {
            $RuleVerified = $true
            break
        }
    }

    if (-not $RuleVerified) {
        throw "Не удалось подтвердить право ReadAndExecute для $($ServiceAccount.UserName) на $LogDirectory."
    }

    $GrantedLogDirectories++
    Write-Host "Проверено: $($ServiceAccount.UserName) имеет ReadAndExecute на $LogDirectory" -ForegroundColor Cyan
}

if ($GrantedLogDirectories -eq 0) {
    Write-Warning "Не подтверждены права чтения ни для одного существующего каталога логов. Перед запуском добавьте все пути из активных масок в параметр -LogDirectories и повторите настройку ACL."
}

$ExePath = Join-Path $InstallDir "OneCIntegrationService.exe"
New-Service -Name $ServiceName `
    -BinaryPathName "`"$ExePath`"" `
    -DisplayName $DisplayName `
    -Description "Мониторинг лог-файлов и передача событий в 1С через V83.COMConnector (x64)" `
    -StartupType Automatic `
    -Credential $ServiceAccount | Out-Null

sc.exe failure $ServiceName reset= 86400 actions= restart/30000/restart/30000/restart/30000 | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Не удалось настроить автоматический перезапуск службы. Код sc.exe: $LASTEXITCODE"
}

Start-Service -Name $ServiceName
$Service = Get-Service -Name $ServiceName
$Service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(30))

Write-Host "Служба $ServiceName успешно установлена и запущена." -ForegroundColor Green
Write-Host "Учетная запись должна иметь право Log on as a service. Проверьте Launch/Activation permissions DCOM для V83.COMConnector (dcomcnfg) и наличие учетных данных Credential Manager в профиле $($ServiceAccount.UserName)." -ForegroundColor Yellow
Write-Host "Убедитесь, что в -LogDirectories перечислены все фактические каталоги лог-файлов из активных масок 1С. Проверка ACL не заменяет проверку прав на сетевых ресурсах и NTFS-родительских каталогах." -ForegroundColor Yellow