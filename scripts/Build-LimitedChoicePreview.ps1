param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory=Join-Path $root 'dist\limited-choice-preview' }
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$f=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc=Join-Path $f 'csc.exe'
if(-not(Test-Path -LiteralPath $csc)){throw 'The .NET Framework compiler was not found.'}
$temporary=Join-Path $env:TEMP ('CodexReminder-ChoiceCompatBuild-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
try {
    # Compatibility build only. The normal source continues using the SID-scoped V5 pipe.
    $source=[IO.File]::ReadAllText((Join-Path $root 'src\SecureTransport.cs'))
    $expected='"CodexCompletionReminderV5-" + WindowsIdentity.GetCurrent().User.Value'
    if(-not $source.Contains($expected)){throw 'Expected pipe definition missing; no compatibility build produced.'}
    $compat=Join-Path $temporary 'SecureTransport.cs'
    [IO.File]::WriteAllText($compat,$source.Replace($expected,'"CodexCompletionReminderV4"'),(New-Object System.Text.UTF8Encoding($false)))
    $appSources=@('ReminderSettings.cs','ReminderSettingsForm.cs','Program.cs','SelfTests.cs','SafetyTests.cs','RequestTracker.cs','CardMonitor.cs','NativeNotificationSettings.cs') |
        ForEach-Object { Join-Path $root ('src\'+$_) }
    & $csc /nologo /target:winexe "/out:$(Join-Path $OutputDirectory 'CodexReminder.exe')" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll $compat @appSources
    if($LASTEXITCODE -ne 0){throw 'Compatibility app build failed.'}
    & $csc /nologo /target:exe "/out:$(Join-Path $OutputDirectory 'CodexReminderScanner.exe')" /reference:System.Web.Extensions.dll "/reference:$(Join-Path $f 'WPF\UIAutomationClient.dll')" "/reference:$(Join-Path $f 'WPF\UIAutomationTypes.dll')" "/reference:$(Join-Path $f 'WPF\WindowsBase.dll')" $compat (Join-Path $root 'src\CardScanner.cs') (Join-Path $root 'src\RequestTracker.cs') (Join-Path $root 'src\ReminderSettings.cs')
    if($LASTEXITCODE -ne 0){throw 'Compatibility scanner build failed.'}
    Write-Output "Built limited choice preview in $OutputDirectory"
    Write-Output 'No install performed. Foreground/readable-card evidence only; minimized, cross-chat and automatic withdrawal remain unverified.'
}
finally {
    $base=[IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')+'\'
    if(-not [IO.Path]::GetFullPath($temporary).StartsWith($base,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe cleanup path.'}
    if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary -Recurse -Force}
}
