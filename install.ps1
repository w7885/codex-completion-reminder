param(
    [switch]$SkipBuild,
    [switch]$BuildFromSource,
    [switch]$NoLaunch,
    [string]$CodexHome,
    [string]$StartupDirectory
)

$ErrorActionPreference = 'Stop'
# Source preview: only isolated tests may install until desktop acceptance is complete.
$defaultHome = [IO.Path]::GetFullPath((Join-Path $env:USERPROFILE '.codex'))
if ([string]::IsNullOrWhiteSpace($CodexHome) -or [string]::IsNullOrWhiteSpace($StartupDirectory) -or
    [IO.Path]::GetFullPath($CodexHome).TrimEnd('\') -eq $defaultHome.TrimEnd('\') -or
    [IO.Path]::GetFullPath($StartupDirectory).TrimEnd('\') -eq [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup).TrimEnd('\')) {
    throw 'Source preview only: production installation is gated on real approval/minimized desktop acceptance. Use isolated CodexHome and StartupDirectory for tests.'
}
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path $root 'dist'
$appSource = Join-Path $dist 'CodexReminder.exe'
$hookSource = Join-Path $dist 'CodexReminderHook.exe'
if ($BuildFromSource -or (-not $SkipBuild -and ((-not (Test-Path -LiteralPath $appSource)) -or (-not (Test-Path -LiteralPath $hookSource))))) {
    & (Join-Path $root 'build.ps1')
}
if (-not (Test-Path -LiteralPath $appSource) -or -not (Test-Path -LiteralPath $hookSource)) {
    throw 'Build outputs are missing. Run build.ps1 first.'
}

if ([string]::IsNullOrWhiteSpace($CodexHome)) { $CodexHome = $env:CODEX_HOME }
if ([string]::IsNullOrWhiteSpace($CodexHome)) { $CodexHome = Join-Path $env:USERPROFILE '.codex' }
$codexHome = [System.IO.Path]::GetFullPath($CodexHome)
$installDir = Join-Path $codexHome 'codex-reminder'
$marker = Join-Path $installDir '.codex-reminder-owned'
$hooksPath = Join-Path $codexHome 'hooks.json'
$startupDir = $StartupDirectory
if ([string]::IsNullOrWhiteSpace($startupDir)) { $startupDir = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup) }
$startupDir = [System.IO.Path]::GetFullPath($startupDir)
$startupPath = Join-Path $startupDir 'CodexCompletionReminder.vbs'
$startupMarker = 'CodexCompletionReminder: managed startup entry'
$legacyStartupMarker = 'CodexCompletionReminder: launches the local helper hidden'

if ((Test-Path -LiteralPath $installDir) -and -not (Test-Path -LiteralPath $marker)) {
    throw "Install directory exists but is not owned by this app: $installDir"
}
if (Test-Path -LiteralPath $startupPath) {
    $existingStartup = Get-Content -Raw -LiteralPath $startupPath
    if (-not ($existingStartup.Contains($startupMarker) -or $existingStartup.Contains($legacyStartupMarker))) {
        throw "Startup entry exists and is not owned by this app: $startupPath"
    }
}

function Test-ReminderHook($handler) {
    if ($null -eq $handler) { return $false }
    $command = [string]$handler.commandWindows + ' ' + [string]$handler.command_windows + ' ' + [string]$handler.command
    return $command -match '(?i)CodexReminderHook\.exe|codex_reminder_hook\.py'
}

function Set-ReminderEventHook($document, [string]$eventName, [string]$commandLine) {
    if ($null -eq $document.hooks) {
        $document | Add-Member -MemberType NoteProperty -Name hooks -Value ([pscustomobject]@{})
    }
    $hooksObject = $document.hooks
    $property = $hooksObject.PSObject.Properties[$eventName]
    $groups = @()
    if ($null -ne $property -and $null -ne $property.Value) { $groups = @($property.Value) }
    $keptGroups = @()
    foreach ($group in $groups) {
        $handlers = @($group.hooks | Where-Object { -not (Test-ReminderHook $_) })
        if ($handlers.Count -gt 0) {
            $group.hooks = $handlers
            $keptGroups += $group
        }
    }
    $managedGroup = [pscustomobject]@{
        hooks = @([pscustomobject]@{
            type = 'command'
            command = $commandLine
            commandWindows = $commandLine
            timeout = 3
        })
    }
    $keptGroups += $managedGroup
    if ($null -eq $property) {
        $hooksObject | Add-Member -MemberType NoteProperty -Name $eventName -Value $keptGroups
    }
    else {
        $property.Value = $keptGroups
    }
}

$oldHooksBytes = $null
$hooksExisted = Test-Path -LiteralPath $hooksPath
$oldStartupBytes = $null
$startupExisted = Test-Path -LiteralPath $startupPath
if ($hooksExisted) {
    $oldHooksBytes = [System.IO.File]::ReadAllBytes($hooksPath)
    try { $document = Get-Content -Raw -LiteralPath $hooksPath | ConvertFrom-Json }
    catch { throw "Cannot parse existing hooks.json; no files were changed. $($_.Exception.Message)" }
    if ($null -eq $document -or $document -isnot [pscustomobject]) { throw 'Unsupported hooks.json shape; no files were changed.' }
}
else {
    $document = [pscustomobject]@{ description = 'User-level Codex lifecycle hooks'; hooks = [pscustomobject]@{} }
}
if ($startupExisted) { $oldStartupBytes = [System.IO.File]::ReadAllBytes($startupPath) }

$createdInstallDir = -not (Test-Path -LiteralPath $installDir)
$appPath = Join-Path $installDir 'CodexReminder.exe'
$hookPath = Join-Path $installDir 'CodexReminderHook.exe'
$backupDir = Join-Path $env:TEMP ('CodexReminderUpgrade-' + [Guid]::NewGuid().ToString('N'))
$hadOldApp = Test-Path -LiteralPath $appPath
$hadOldHook = Test-Path -LiteralPath $hookPath
if ($hadOldApp -or $hadOldHook) {
    New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
    if ($hadOldApp) { Copy-Item -LiteralPath $appPath -Destination (Join-Path $backupDir 'CodexReminder.exe') }
    if ($hadOldHook) { Copy-Item -LiteralPath $hookPath -Destination (Join-Path $backupDir 'CodexReminderHook.exe') }
}

$runningHelpers = Get-Process -Name 'CodexReminder' -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -eq $appPath } catch { $false }
}
if ($runningHelpers) { $runningHelpers | Stop-Process -Force -ErrorAction SilentlyContinue }

try {
    New-Item -ItemType Directory -Force -Path $installDir,$startupDir | Out-Null
    Copy-Item -LiteralPath $appSource -Destination (Join-Path $installDir 'CodexReminder.exe') -Force
    Copy-Item -LiteralPath $hookSource -Destination (Join-Path $installDir 'CodexReminderHook.exe') -Force
    [System.IO.File]::WriteAllText($marker, "Codex Windows Completion Reminder`r`n", (New-Object System.Text.UTF8Encoding($false)))

    # A bare quoted executable path is a string expression in PowerShell.
    # Use an explicit invocation and the JSON schema's Windows override key.
    $escapedHookPath = (Join-Path $installDir 'CodexReminderHook.exe').Replace("'", "''")
    $hookCommand = 'powershell.exe -NoProfile -NonInteractive -Command "& ''' + $escapedHookPath + '''"'
    Set-ReminderEventHook $document 'Stop' $hookCommand
    Set-ReminderEventHook $document 'PermissionRequest' $hookCommand
    $hooksJson = ConvertTo-Json -InputObject $document -Depth 100
    $hooksTemp = $hooksPath + '.tmp'
    [System.IO.File]::WriteAllText($hooksTemp, $hooksJson + "`r`n", (New-Object System.Text.UTF8Encoding($false)))
    if ($hooksExisted) {
        $backup = $hooksPath + '.backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
        [System.IO.File]::Replace($hooksTemp, $hooksPath, $backup, $true)
    }
    else {
        [System.IO.File]::Move($hooksTemp, $hooksPath)
    }

    $quotedAppPath = $appPath.Replace('"', '""')
    $vbs = @(
        "' $startupMarker",
        'Set shell = CreateObject("WScript.Shell")',
        ('shell.Run """{0}""", 0, False' -f $quotedAppPath)
    ) -join "`r`n"
    $vbs += "`r`n"
    [System.IO.File]::WriteAllText($startupPath, $vbs, (New-Object System.Text.UTF8Encoding($false)))
}
catch {
    if ($hooksExisted -and $null -ne $oldHooksBytes) {
        [System.IO.File]::WriteAllBytes($hooksPath, $oldHooksBytes)
    }
    elseif (-not $hooksExisted -and (Test-Path -LiteralPath $hooksPath)) {
        Remove-Item -LiteralPath $hooksPath -Force
    }
    if ($startupExisted -and $null -ne $oldStartupBytes) {
        [System.IO.File]::WriteAllBytes($startupPath, $oldStartupBytes)
    }
    elseif (-not $startupExisted -and (Test-Path -LiteralPath $startupPath)) {
        Remove-Item -LiteralPath $startupPath -Force
    }
    if ($createdInstallDir -and (Test-Path -LiteralPath $installDir)) {
        Remove-Item -LiteralPath $installDir -Recurse -Force
    }
    elseif (Test-Path -LiteralPath $backupDir) {
        if ($hadOldApp) { Copy-Item -LiteralPath (Join-Path $backupDir 'CodexReminder.exe') -Destination $appPath -Force }
        if ($hadOldHook) { Copy-Item -LiteralPath (Join-Path $backupDir 'CodexReminderHook.exe') -Destination $hookPath -Force }
    }
    if (Test-Path -LiteralPath $backupDir) { Remove-Item -LiteralPath $backupDir -Recurse -Force }
    throw
}

if (Test-Path -LiteralPath $backupDir) { Remove-Item -LiteralPath $backupDir -Recurse -Force }

Write-Output 'Installed Codex Completion Reminder.'
Write-Output "App: $installDir"
Write-Output "Hooks: $hooksPath (Stop, PermissionRequest)"
Write-Output "Startup: $startupPath"
Write-Output 'Restart Codex and review/trust the updated Hook definitions. Native Codex notification settings were not changed.'
if (-not $NoLaunch -and -not $PSBoundParameters.ContainsKey('CodexHome')) {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    $isElevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if ($isElevated) {
        Write-Output 'Install ran elevated; the app was not started elevated. It will launch at the next user login; you may also start it manually now.'
    }
    else {
        Start-Process -FilePath $appPath -WorkingDirectory $installDir
        Write-Output 'The tray companion was started in this user session.'
    }
}
