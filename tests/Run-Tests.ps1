$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$build = Join-Path $root 'build.ps1'
& $build
if ($LASTEXITCODE -ne 0) { throw 'Build or application self-tests failed.' }

$testRoot = Join-Path $env:TEMP ('Codex Reminder Tests-' + [Guid]::NewGuid().ToString('N'))
$testCodexHome = Join-Path $testRoot 'codex-home'
$testStartup = Join-Path $testRoot 'startup'
$previousNoStart = $env:CODEX_REMINDER_TEST_NO_START
$env:CODEX_REMINDER_TEST_NO_START = "1"
$previousCodexHome = $env:CODEX_HOME
$env:CODEX_HOME = $testCodexHome
New-Item -ItemType Directory -Force -Path $testCodexHome,$testStartup | Out-Null

try {
    $hookPath = Join-Path $root 'dist\CodexReminderHook.exe'
    $stopPayload = '{"hook_event_name":"Stop","turn_id":"test-turn-1"}'
    $stopOutput = $stopPayload | & $hookPath
    if ($LASTEXITCODE -ne 0 -or ($stopOutput -join '') -notmatch '"continue"\s*:\s*true') {
        throw 'Stop hook must return continue:true without changing turn behavior.'
    }

    $permissionPayload = '{"hook_event_name":"PermissionRequest","turn_id":"test-turn-2","tool_name":"Bash","tool_input":{"command":"test"}}'
    $permissionOutput = $permissionPayload | & $hookPath
    if ($LASTEXITCODE -ne 0 -or -not [string]::IsNullOrWhiteSpace(($permissionOutput -join ''))) {
        throw 'PermissionRequest hook must return no decision or stdout content.'
    }

    foreach ($payload in @('{broken', '{"hook_event_name":"Interrupt"}', '{"hook_event_name":"Unknown"}', ('x' * 65537), '{"hook_event_name":"PermissionRequest","tool_input":{"decision":"allow"}}')) {
        $result = $payload | & $hookPath
        if ($LASTEXITCODE -ne 0 -or -not [string]::IsNullOrWhiteSpace(($result -join ''))) { throw 'Rejected or permission input must not emit decisions or fail the Codex turn.' }
    }
    $before = if (Test-Path (Join-Path $env:LOCALAPPDATA 'CodexCompletionReminder\hook-status.log')) { (Get-Content (Join-Path $env:LOCALAPPDATA 'CodexCompletionReminder\hook-status.log')).Count } else { 0 }
    $permissionPayload | & $hookPath | Out-Null
    $newLog = @(Get-Content (Join-Path $env:LOCALAPPDATA 'CodexCompletionReminder\hook-status.log') | Select-Object -Skip $before)
    if ($newLog -match 'pipe-written') { throw 'PermissionRequest must not trigger transport.' }
    $hooksPath = Join-Path $testCodexHome 'hooks.json'
    $configPath = Join-Path $testCodexHome 'config.toml'
    $nativeConfig = "notifications-turn-mode = `"always`"`r`nnotify = [`"example-native-notify`"]`r`n"
    [System.IO.File]::WriteAllText($configPath, $nativeConfig, (New-Object System.Text.UTF8Encoding($false)))
    $seed = [pscustomobject]@{
        description = 'Existing test hooks'
        hooks = [pscustomobject]@{
            Stop = @([pscustomobject]@{
                matcher = 'keep-me'
                hooks = @(
                    [pscustomobject]@{ type = 'command'; command = 'powershell.exe -NoProfile -Command "Write-Output keep"' },
                    [pscustomobject]@{ type = 'command'; command = 'pythonw.exe codex_reminder_hook.py' }
                )
            })
            UserPromptSubmit = @([pscustomobject]@{
                hooks = @([pscustomobject]@{ type = 'command'; command = 'user-hook.exe' })
            })
        }
    }
    [System.IO.File]::WriteAllText($hooksPath, (ConvertTo-Json $seed -Depth 30), (New-Object System.Text.UTF8Encoding($false)))
    $startupFile = Join-Path $testStartup 'CodexCompletionReminder.vbs'
    [System.IO.File]::WriteAllText($startupFile, "' CodexCompletionReminder: launches the local helper hidden`r`n", (New-Object System.Text.UTF8Encoding($false)))

    & (Join-Path $root 'install.ps1') -SkipBuild -NoLaunch -CodexHome $testCodexHome -StartupDirectory $testStartup
    if ($LASTEXITCODE -ne 0) { throw 'Isolated install test failed.' }
    $installed = Get-Content -Raw $hooksPath | ConvertFrom-Json
    $stopCommands = @($installed.hooks.Stop | ForEach-Object { $_.hooks } | ForEach-Object { $_.command })
    if (-not ($stopCommands -contains 'powershell.exe -NoProfile -Command "Write-Output keep"')) { throw 'Install removed an unrelated Stop hook.' }
    if (-not (($stopCommands -join ' ') -match 'CodexReminderHook\.exe')) { throw 'Install did not register the Stop hook.' }
    if (@($stopCommands | Where-Object { $_ -match 'CodexReminderHook\.exe' }).Count -ne 1) { throw 'Install left duplicate managed Stop hooks.' }
    $permissionCommands = @($installed.hooks.PermissionRequest | ForEach-Object { $_.hooks } | ForEach-Object { $_.command })
    if (-not (($permissionCommands -join ' ') -match 'CodexReminderHook\.exe')) { throw 'Install did not register PermissionRequest.' }
    $managed = @($installed.hooks.Stop | ForEach-Object { $_.hooks } | Where-Object { $_.command -match 'CodexReminderHook\.exe' })[0]
    if ($managed.commandWindows -ne $managed.command) { throw 'Windows JSON command override is missing.' }
    # Execute the complete installed command without losing its nested quotes in Windows PowerShell 5.1.
    $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($managed.commandWindows))
    $shellOutput = $stopPayload | powershell.exe -NoProfile -NonInteractive -EncodedCommand $encodedCommand
    if ($LASTEXITCODE -ne 0 -or ($shellOutput -join '') -notmatch '^\s*\{.*"continue"\s*:\s*true.*\}\s*$') {
        throw 'Installed command must execute the bridge through PowerShell, including paths with spaces.'
    }
    $approvalOutput = $permissionPayload | powershell.exe -NoProfile -NonInteractive -EncodedCommand $encodedCommand
    if ($LASTEXITCODE -ne 0 -or -not [string]::IsNullOrWhiteSpace(($approvalOutput -join ''))) {
        throw 'Installed approval command must preserve the no-decision stdout contract.'
    }
    if (-not (Test-Path $startupFile)) { throw 'Install did not create its Startup entry.' }
    $startupText = Get-Content -Raw $startupFile
    if ($startupText -notmatch 'shell\.Run\s+""".*CodexReminder\.exe""",\s*0,\s*False') { throw 'Startup entry does not safely quote the executable path.' }
    if ($startupText -notmatch 'CodexCompletionReminder: managed startup entry') { throw 'Install did not migrate the app-owned legacy Startup entry.' }
    if ([System.IO.File]::ReadAllText($configPath) -ne $nativeConfig) { throw 'Install changed Codex native notification settings.' }

    & (Join-Path $root 'uninstall.ps1') -CodexHome $testCodexHome -StartupDirectory $testStartup
    if ($LASTEXITCODE -ne 0) { throw 'Isolated uninstall test failed.' }
    $removed = Get-Content -Raw $hooksPath | ConvertFrom-Json
    $remainingStop = @($removed.hooks.Stop | ForEach-Object { $_.hooks } | ForEach-Object { $_.command })
    if (-not ($remainingStop -contains 'powershell.exe -NoProfile -Command "Write-Output keep"')) { throw 'Uninstall removed an unrelated Stop hook.' }
    if (($remainingStop -join ' ') -match 'CodexReminderHook\.exe|codex_reminder_hook\.py') { throw 'Uninstall left a managed Stop hook.' }
    if ($removed.hooks.PSObject.Properties['PermissionRequest']) { throw 'Uninstall left the managed PermissionRequest hook.' }
    if ($removed.hooks.PSObject.Properties['UserPromptSubmit'] -eq $null) { throw 'Uninstall removed an unrelated UserPromptSubmit hook.' }
    if (Test-Path (Join-Path $testStartup 'CodexCompletionReminder.vbs')) { throw 'Uninstall left the app-owned Startup entry.' }
    if (Test-Path (Join-Path $testCodexHome 'codex-reminder')) { throw 'Uninstall left the app-owned install directory.' }
    if ([System.IO.File]::ReadAllText($configPath) -ne $nativeConfig) { throw 'Uninstall changed Codex native notification settings.' }

    Write-Output 'All hook contract and isolated install/uninstall tests passed.'
}
finally {
    $env:CODEX_REMINDER_TEST_NO_START = $previousNoStart
    if ($null -eq $previousCodexHome) { Remove-Item Env:\CODEX_HOME -ErrorAction SilentlyContinue }
    else { $env:CODEX_HOME = $previousCodexHome }
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
