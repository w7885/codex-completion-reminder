param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $csc)) {
    throw 'The .NET Framework C# compiler was not found. Install the Windows .NET Framework developer tools.'
}

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$appArgs = @(
    '/nologo', '/target:winexe', "/out:$(Join-Path $dist 'CodexReminder.exe')",
    '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', '/reference:System.Web.Extensions.dll',
    (Join-Path $root 'src\ReminderSettings.cs'),
    (Join-Path $root 'src\ReminderSettingsForm.cs'),
    (Join-Path $root 'src\Program.cs'),
    (Join-Path $root 'src\SelfTests.cs'),
    (Join-Path $root 'src\SafetyTests.cs'),
    (Join-Path $root 'src\SecureTransport.cs'),
    (Join-Path $root 'src\RequestTracker.cs'),
    (Join-Path $root 'src\CardMonitor.cs'),
    (Join-Path $root 'src\NativeNotificationSettings.cs')
)
& $csc @appArgs
if ($LASTEXITCODE -ne 0) { throw "Reminder app compilation failed ($LASTEXITCODE)." }

$hookArgs = @(
    '/nologo', '/target:exe', "/out:$(Join-Path $dist 'CodexReminderHook.exe')",
    '/reference:System.Web.Extensions.dll',
    (Join-Path $root 'src\ReminderSettings.cs'),
    (Join-Path $root 'src\CodexReminderHook.cs'), (Join-Path $root 'src\SecureTransport.cs')
)
& $csc @hookArgs
if ($LASTEXITCODE -ne 0) { throw "Hook bridge compilation failed ($LASTEXITCODE)." }

& (Join-Path $dist 'CodexReminder.exe') --self-test | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'The application self-tests failed.' }
$scannerArgs = @('/nologo', '/target:exe', "/out:$(Join-Path $dist 'CodexReminderScanner.exe')",
    '/reference:System.Web.Extensions.dll',
    "/reference:$(Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\WPF\UIAutomationClient.dll')",
    "/reference:$(Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\WPF\UIAutomationTypes.dll')",
    "/reference:$(Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\WPF\WindowsBase.dll')",
    (Join-Path $root 'src\CardScanner.cs'), (Join-Path $root 'src\RequestTracker.cs'),
    (Join-Path $root 'src\SecureTransport.cs'), (Join-Path $root 'src\ReminderSettings.cs'))
& $csc @scannerArgs
if ($LASTEXITCODE -ne 0) { throw 'Scanner compilation failed.' }
Write-Output "Built source preview binaries in $dist; desktop acceptance required before installation."
