$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$output = Join-Path $root 'dist\ChoiceFilterTests.exe'
& $compiler /nologo /target:exe /reference:System.Web.Extensions.dll "/out:$output" (Join-Path $root 'tests\ChoiceFilterTests.cs') (Join-Path $root 'src\ChoiceCompletionFilter.cs') (Join-Path $root 'src\SecureTransport.cs')
if ($LASTEXITCODE -ne 0) { throw 'Choice filter tests did not compile.' }
& $output
if ($LASTEXITCODE -ne 0) { throw 'Choice filter metadata checks failed.' }
& $output --hook (Join-Path $root 'dist\CodexReminderHook.exe')
if ($LASTEXITCODE -ne 0) { throw 'Choice filter Hook integration failed.' }
