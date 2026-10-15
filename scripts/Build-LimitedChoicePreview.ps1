param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory=Join-Path $root 'dist\limited-choice-preview' }
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$appIcon=Join-Path $root 'assets\CodexReminder.ico'
if(-not(Test-Path -LiteralPath $appIcon)){throw 'Application icon is missing: assets\CodexReminder.ico'}
$f=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc=Join-Path $f 'csc.exe'
if(-not(Test-Path -LiteralPath $csc)){throw 'The .NET Framework compiler was not found.'}
$temporary=Join-Path $env:TEMP ('CodexReminder-ChoiceCompatBuild-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
try {
    # All entry points now build the same V4 transport. Never create a V5 variant.
    $source=[IO.File]::ReadAllText((Join-Path $root 'src\SecureTransport.cs'))
    $expected='return "CodexCompletionReminderV4";'
    if(-not $source.Contains($expected)){throw 'Expected pipe definition missing; no compatibility build produced.'}
    $compat=Join-Path $temporary 'SecureTransport.cs'
    [IO.File]::WriteAllText($compat,$source,(New-Object System.Text.UTF8Encoding($false)))
    $appSources=@('ReminderSettings.cs','ReminderSettingsForm.cs','Program.cs','SelfTests.cs','SafetyTests.cs','RequestTracker.cs','CardMonitor.cs','NativeNotificationSettings.cs') |
        ForEach-Object { Join-Path $root ('src\'+$_) }
    & $csc /nologo /target:winexe /define:CODEX_REMINDER_V4 "/win32icon:$appIcon" "/out:$(Join-Path $OutputDirectory 'CodexReminder.exe')" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll $compat @appSources
    if($LASTEXITCODE -ne 0){throw 'Compatibility app build failed.'}
    & $csc /nologo /target:exe /define:CODEX_REMINDER_V4 "/out:$(Join-Path $OutputDirectory 'CodexReminderScanner.exe')" /reference:System.Web.Extensions.dll $compat (Join-Path $root 'src\CardScanner.cs') (Join-Path $root 'src\RequestTracker.cs') (Join-Path $root 'src\ReminderSettings.cs') (Join-Path $root 'src\ChoiceCompletionFilter.cs')
    if($LASTEXITCODE -ne 0){throw 'Compatibility scanner build failed.'}
    & $csc /nologo /target:exe "/out:$(Join-Path $OutputDirectory 'CodexReminderHook.exe')" /reference:System.Web.Extensions.dll $compat (Join-Path $root 'src\CodexReminderHook.cs') (Join-Path $root 'src\ReminderSettings.cs') (Join-Path $root 'src\ChoiceCompletionFilter.cs')
    if($LASTEXITCODE -ne 0){throw 'V4 Hook build failed.'}
    Write-Output "Built limited choice preview in $OutputDirectory"
    Write-Output 'No install performed. Metadata choice discovery requires real background and cross-chat desktop acceptance.'
}
finally {
    $base=[IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')+'\'
    if(-not [IO.Path]::GetFullPath($temporary).StartsWith($base,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe cleanup path.'}
    if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary -Recurse -Force}
}
