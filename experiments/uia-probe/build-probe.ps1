$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$output = Join-Path $root 'dist\CodexReminderUiaProbe.exe'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $output) | Out-Null
& (Join-Path $framework 'csc.exe') /nologo /target:exe "/out:$output" /reference:System.Web.Extensions.dll "/reference:$(Join-Path $framework 'WPF\UIAutomationClient.dll')" "/reference:$(Join-Path $framework 'WPF\UIAutomationTypes.dll')" "/reference:$(Join-Path $framework 'WPF\WindowsBase.dll')" (Join-Path $PSScriptRoot 'Probe.cs')
if ($LASTEXITCODE -ne 0) { throw 'Read-only UIA probe compilation failed.' }
Write-Output "Built read-only probe: $output"
