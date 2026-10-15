$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$output=Join-Path $root 'dist\BackgroundChoiceTests.exe'
& $compiler /nologo /target:exe /main:BackgroundChoiceTests /reference:System.Web.Extensions.dll "/out:$output" (Join-Path $root 'tests\BackgroundChoiceTests.cs') (Join-Path $root 'src\ChoiceCompletionFilter.cs') (Join-Path $root 'src\SecureTransport.cs') (Join-Path $root 'src\RequestTracker.cs') (Join-Path $root 'src\CardMonitor.cs') (Join-Path $root 'src\CardScanner.cs') (Join-Path $root 'src\ReminderSettings.cs')
if($LASTEXITCODE -ne 0){throw 'Background choice test compilation failed.'}
& $output
if($LASTEXITCODE -ne 0){throw 'Background choice tests failed.'}
