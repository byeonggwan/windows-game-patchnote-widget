# Builds PatchNoteMailbox.exe (BWUI + widget sources) with the C# compiler that ships with Windows (.NET Framework 4.x).
#   .\build.ps1              build bin\PatchNoteMailbox.exe only
#   .\build.ps1 -Snapshots   also render docs\*.png (light and dark)
#   .\build.ps1 -Install     also stop the running widget and copy the exe to %LOCALAPPDATA%\PatchNoteMailbox
param([switch]$Install, [switch]$Snapshots)
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$fw = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
$out = Join-Path $src 'bin'
$exe = Join-Path $out 'PatchNoteMailbox.exe'

New-Item -ItemType Directory -Force $out | Out-Null

$refs = @(
    "$fw\WPF\PresentationFramework.dll",
    "$fw\WPF\PresentationCore.dll",
    "$fw\WPF\WindowsBase.dll",
    "$fw\System.Xaml.dll",
    "$fw\System.Web.Extensions.dll",
    "$fw\System.Net.Http.dll",
    "$fw\System.Xml.Linq.dll"
) | ForEach-Object { "/r:$_" }

$files = @(Get-ChildItem "$src\BWUI\*.cs") + @(Get-Item "$src\Program.cs") +
    @(Get-ChildItem "$src\Models\*.cs") + @(Get-ChildItem "$src\Services\*.cs") + @(Get-ChildItem "$src\UI\*.cs") |
    ForEach-Object { $_.FullName }

& $csc /nologo /codepage:65001 /optimize+ /target:winexe "/win32manifest:$src\BWUI\bwui.manifest" "/out:$exe" @refs @files
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Write-Host "Built $exe"

if ($Snapshots) {
    $docs = Join-Path $src 'docs'
    New-Item -ItemType Directory -Force $docs | Out-Null
    foreach ($theme in 'light', 'dark') {
        foreach ($mode in 'expanded', 'collapsed', 'popover') {
            $a = @('--snapshot', (Join-Path $docs "$mode-$theme.png"), $theme)
            if ($mode -ne 'expanded') { $a += $mode }
            Start-Process $exe -ArgumentList $a -Wait
        }
    }
    Write-Host "Snapshots written to $docs"
}

if ($Install) {
    $dest = Join-Path $env:LOCALAPPDATA 'PatchNoteMailbox'
    New-Item -ItemType Directory -Force $dest | Out-Null
    Get-Process PatchNoteMailbox -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 300
    Copy-Item $exe $dest -Force
    Write-Host "Installed to $dest\PatchNoteMailbox.exe"
}
