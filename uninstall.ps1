param(
    [switch]$RemoveSettings,
    [string]$CodexHome,
    [string]$StartupDirectory
)

$ErrorActionPreference = 'Stop'
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

function Test-ReminderHook($handler) {
    if ($null -eq $handler) { return $false }
    $command = [string]$handler.commandWindows + ' ' + [string]$handler.command_windows + ' ' + [string]$handler.command
    return $command -match '(?i)CodexReminderHook\.exe|codex_reminder_hook\.py'
}

function Remove-ReminderEventHook($document, [string]$eventName) {
    if ($null -eq $document.hooks) { return }
    $hooksObject = $document.hooks
    $property = $hooksObject.PSObject.Properties[$eventName]
    if ($null -eq $property) { return }
    $keptGroups = @()
    foreach ($group in @($property.Value)) {
        $handlers = @($group.hooks | Where-Object { -not (Test-ReminderHook $_) })
        if ($handlers.Count -gt 0) {
            $group.hooks = $handlers
            $keptGroups += $group
        }
    }
    if ($keptGroups.Count -eq 0) { $hooksObject.PSObject.Properties.Remove($eventName) }
    else { $property.Value = $keptGroups }
}

if (Test-Path -LiteralPath $hooksPath) {
    $original = [System.IO.File]::ReadAllBytes($hooksPath)
    try { $document = Get-Content -Raw -LiteralPath $hooksPath | ConvertFrom-Json }
    catch { throw "Cannot parse hooks.json; no configuration was changed. $($_.Exception.Message)" }
    Remove-ReminderEventHook $document 'Stop'
    Remove-ReminderEventHook $document 'PermissionRequest'
    $updated = ConvertTo-Json -InputObject $document -Depth 100
    $temporary = $hooksPath + '.tmp'
    [System.IO.File]::WriteAllText($temporary, $updated + "`r`n", (New-Object System.Text.UTF8Encoding($false)))
    try { [System.IO.File]::Replace($temporary, $hooksPath, $hooksPath + '.uninstall-backup', $true) }
    catch {
        [System.IO.File]::WriteAllBytes($hooksPath + '.uninstall-backup', $original)
        [System.IO.File]::Copy($temporary, $hooksPath, $true)
        Remove-Item -LiteralPath $temporary -Force
    }
}

if (Test-Path -LiteralPath $startupPath) {
    $existingStartup = Get-Content -Raw -LiteralPath $startupPath
    if ($existingStartup.Contains($startupMarker) -or $existingStartup.Contains($legacyStartupMarker)) {
        Remove-Item -LiteralPath $startupPath -Force
    }
    else {
        Write-Output 'Startup entry is not app-owned; leaving it untouched.'
    }
}
else {
    Write-Output 'No app-owned startup entry was found; leaving any unrelated entry untouched.'
}

if (Test-Path -LiteralPath $marker) {
    $appPath = Join-Path $installDir 'CodexReminder.exe'
    Get-Process -Name 'CodexReminder' -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -eq $appPath } catch { $false }
    } | Stop-Process -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $installDir -Recurse -Force
}
else {
    Write-Output 'Install directory is not marked as app-owned; leaving it untouched.'
}

if ($RemoveSettings) {
    $settingsDir = Join-Path $env:LOCALAPPDATA 'CodexCompletionReminder'
    if (Test-Path -LiteralPath $settingsDir) { Remove-Item -LiteralPath $settingsDir -Recurse -Force }
}
else {
    Write-Output 'User settings were kept. Pass -RemoveSettings only if you want to delete them.'
}

Write-Output 'Uninstalled managed hooks and startup entry. Restart Codex to reload hooks.'
