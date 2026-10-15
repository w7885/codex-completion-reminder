$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$output=Join-Path $root 'dist\PopupLifetimeTests.exe'
$sources=@('Program.cs','ReminderSettings.cs','ReminderSettingsForm.cs','SelfTests.cs','SafetyTests.cs','SecureTransport.cs','RequestTracker.cs','CardMonitor.cs','NativeNotificationSettings.cs') | ForEach-Object { Join-Path $root ('src\'+$_) }
& $compiler /nologo /target:exe /main:PopupLifetimeTests /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll "/out:$output" @sources (Join-Path $PSScriptRoot 'PopupLifetimeTests.cs')
if($LASTEXITCODE -ne 0){throw 'Popup lifetime test compilation failed.'}
& $output
if($LASTEXITCODE -ne 0){throw 'Popup lifetime tests failed.'}
