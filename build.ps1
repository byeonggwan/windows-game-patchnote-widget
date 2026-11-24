# Builds PatchNoteMailbox.exe with the built-in Windows C# compiler and installs it to %LOCALAPPDATA%\PatchNoteMailbox.
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$fw = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
$dest = Join-Path $env:LOCALAPPDATA 'PatchNoteMailbox'
$out = Join-Path $src 'bin'

New-Item -ItemType Directory -Force $out, $dest | Out-Null

$refs = @(
    "$fw\WPF\PresentationFramework.dll",
    "$fw\WPF\PresentationCore.dll",
    "$fw\WPF\WindowsBase.dll",
    "$fw\System.Xaml.dll",
    "$fw\System.Web.Extensions.dll",
    "$fw\System.Net.Http.dll",
    "$fw\System.Xml.Linq.dll"
) | ForEach-Object { "/r:$_" }

$files = @(
    "$src\Program.cs",
    "$src\Models\Models.cs",
    "$src\Services\StorageService.cs",
    "$src\Services\PatchNoteFetcher.cs",
    "$src\UI\DockWindow.cs",
    "$src\UI\PopoverWindow.cs"
)

& $csc /nologo /codepage:65001 /optimize+ /target:winexe "/out:$out\PatchNoteMailbox.exe" @refs @files
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

Get-Process PatchNoteMailbox -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300
Copy-Item "$out\PatchNoteMailbox.exe" $dest -Force
Write-Host "Built and installed to $dest\PatchNoteMailbox.exe"
