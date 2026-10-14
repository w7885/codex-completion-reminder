$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$build = Join-Path $root 'build.ps1'
& $build
if ($LASTEXITCODE -ne 0) { throw 'Build or application self-tests failed.' }
& (Join-Path $PSScriptRoot 'Run-ChoiceFilterTests.ps1')

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
    # Shared production logs may contain concurrent Stop events; do not use them as test evidence.
    $permissionDiagnostic = $permissionPayload | & $hookPath --diagnostic
    if ($LASTEXITCODE -ne 0 -or -not [string]::IsNullOrWhiteSpace(($permissionDiagnostic -join ''))) {
        throw 'PermissionRequest must remain a no-op even in diagnostic mode.'
    }
    $legacyDirectory = Join-Path $testRoot 'legacy-v4'
    & (Join-Path $root 'scripts\Build-LegacyHook.ps1') -OutputDirectory $legacyDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Legacy V4 hook build failed.' }
    $legacyHook = Join-Path $legacyDirectory 'CodexReminderHook.exe'
    foreach ($payload in @($permissionPayload, '{"hook_event_name":"Interrupt"}', '{invalid', ('x' * 65537))) {
        $legacyOutput = $payload | & $legacyHook --diagnostic
        if ($LASTEXITCODE -ne 0 -or -not [string]::IsNullOrWhiteSpace(($legacyOutput -join ''))) {
            throw 'Legacy V4 approval/interrupt/rejected input must not notify or return a decision.'
        }
    }
    foreach ($executable in @($hookPath, $legacyHook)) {
        foreach ($bom in @($false, $true)) {
            $info = New-Object Diagnostics.ProcessStartInfo
            $info.FileName = $executable
            $info.UseShellExecute = $false
            $info.CreateNoWindow = $true
            $info.RedirectStandardInput = $true
            $info.RedirectStandardOutput = $true
            $info.RedirectStandardError = $true
            $process = New-Object Diagnostics.Process
            $process.StartInfo = $info
            [void]$process.Start()
            $testChinese = [string][char]0x4EFB + [char]0x52A1 + [char]0x5B8C + [char]0x6210
            $json = '{"hook_event_name":"Stop","turn_id":"utf8-test","last_assistant_message":"' + $testChinese + '"}'
            if ($bom) { $json = [string][char]0xFEFF + $json }
            $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes($json)
            $process.StandardInput.BaseStream.Write($bytes, 0, $bytes.Length)
            $process.StandardInput.Close()
            if (-not $process.WaitForExit(4000)) { $process.Kill(); throw 'UTF-8 hook timed out.' }
            $result = $process.StandardOutput.ReadToEnd()
            if ($process.ExitCode -ne 0 -or $result -notmatch '"continue"\s*:\s*true') { throw ('UTF-8/BOM Stop input failed; bom=' + $bom + '; executable=' + [IO.Path]::GetFileName($executable) + '; exit=' + $process.ExitCode + '; stdoutLength=' + $result.Length) }
            $process.Dispose()
        }
    }
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
            PermissionRequest = @([pscustomobject]@{
                matcher = 'keep-approval-matcher'
                hooks = @(
                    [pscustomobject]@{ type = 'command'; command = 'other-approval-hook.exe' },
                    [pscustomobject]@{ type = 'command'; command = 'CodexReminderHook.exe'; commandWindows = 'CodexReminderHook.exe' }
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
    if (($permissionCommands -join ' ') -match 'CodexReminderHook\.exe|codex_reminder_hook\.py') { throw 'Install left a legacy app-owned PermissionRequest hook.' }
    if ($permissionCommands.Count -ne 1 -or $permissionCommands[0] -ne 'other-approval-hook.exe' -or $installed.hooks.PermissionRequest[0].matcher -ne 'keep-approval-matcher') { throw 'Install changed an unrelated approval hook or matcher.' }
    & (Join-Path $root 'install.ps1') -SkipBuild -NoLaunch -CodexHome $testCodexHome -StartupDirectory $testStartup
    if ($LASTEXITCODE -ne 0) { throw 'Repeated isolated install failed.' }
    $reinstalled = Get-Content -Raw $hooksPath | ConvertFrom-Json
    $reinstalledApproval = @($reinstalled.hooks.PermissionRequest | ForEach-Object { $_.hooks } | ForEach-Object { $_.command })
    if ($reinstalledApproval.Count -ne 1 -or $reinstalledApproval[0] -ne 'other-approval-hook.exe') { throw 'Repeated install did not preserve unrelated approval hooks.' }
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
    $remainingApproval = @($removed.hooks.PermissionRequest | ForEach-Object { $_.hooks } | ForEach-Object { $_.command })
    if ($remainingApproval.Count -ne 1 -or $remainingApproval[0] -ne 'other-approval-hook.exe') { throw 'Uninstall changed an unrelated approval hook.' }
    if ($removed.hooks.PSObject.Properties['UserPromptSubmit'] -eq $null) { throw 'Uninstall removed an unrelated UserPromptSubmit hook.' }
    if (Test-Path (Join-Path $testStartup 'CodexCompletionReminder.vbs')) { throw 'Uninstall left the app-owned Startup entry.' }
    if (Test-Path (Join-Path $testCodexHome 'codex-reminder')) { throw 'Uninstall left the app-owned install directory.' }
    if ([System.IO.File]::ReadAllText($configPath) -ne $nativeConfig) { throw 'Uninstall changed Codex native notification settings.' }

    # A legacy approval group containing only this app must disappear entirely.
    $removed.hooks.PermissionRequest = @([pscustomobject]@{
        hooks = @([pscustomobject]@{ type = 'command'; command = 'CodexReminderHook.exe' })
    })
    [IO.File]::WriteAllText($hooksPath, (ConvertTo-Json $removed -Depth 100), (New-Object Text.UTF8Encoding($false)))
    & (Join-Path $root 'install.ps1') -SkipBuild -NoLaunch -CodexHome $testCodexHome -StartupDirectory $testStartup
    if ($LASTEXITCODE -ne 0) { throw 'Legacy-only migration install failed.' }
    $migrated = Get-Content -Raw $hooksPath | ConvertFrom-Json
    if ($migrated.hooks.PSObject.Properties['PermissionRequest']) { throw 'Legacy-only app approval group was not removed.' }
    & (Join-Path $root 'uninstall.ps1') -CodexHome $testCodexHome -StartupDirectory $testStartup
    if ($LASTEXITCODE -ne 0) { throw 'Legacy-only migration cleanup failed.' }
    Write-Output 'All hook contract and isolated install/uninstall tests passed.'
}
finally {
    $env:CODEX_REMINDER_TEST_NO_START = $previousNoStart
    if ($null -eq $previousCodexHome) { Remove-Item Env:\CODEX_HOME -ErrorAction SilentlyContinue }
    else { $env:CODEX_HOME = $previousCodexHome }
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
