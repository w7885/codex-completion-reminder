param(
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root 'dist\legacy-v4'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $csc)) { throw 'The .NET Framework compiler is unavailable.' }

# Build the completion-only handler from the same source as the V5 preview.
# Only the completion pipe name differs to match the already installed V4 tray app.
$source = [IO.File]::ReadAllText((Join-Path $root 'src\CodexReminderHook.cs'))
$expected = 'private static string PipeName { get { return SecureTransport.PipeName; } }'
if (-not $source.Contains($expected) -or -not $source.Contains('kind = ReminderKinds.Completion;')) {
    throw 'The expected safe hook source was not found; no output built.'
}
$legacy = $source.Replace($expected, 'private const string PipeName = "CodexCompletionReminderV4";')
$temporary = Join-Path $env:TEMP ('CodexReminder-LegacyBuild-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
try {
    $sourceFile = Join-Path $temporary 'LegacyHook.cs'
    [IO.File]::WriteAllText($sourceFile, $legacy, (New-Object System.Text.UTF8Encoding($false)))
    $output = Join-Path $OutputDirectory 'CodexReminderHook.exe'
    & $csc /nologo /target:exe "/out:$output" /reference:System.Web.Extensions.dll $sourceFile (Join-Path $root 'src\SecureTransport.cs') (Join-Path $root 'src\ReminderSettings.cs') (Join-Path $root 'src\ChoiceCompletionFilter.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Legacy hook compilation failed.' }
    Write-Output "Built legacy V4 hook: $output"
    Write-Output 'Build only: no installed executable, hook definition, settings or startup item was changed.'
}
finally {
    # Delete only this freshly created GUID directory beneath the resolved TEMP directory.
    $resolvedTemp = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
    $resolvedBuild = [IO.Path]::GetFullPath($temporary)
    if (-not $resolvedBuild.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe temporary directory.' }
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
