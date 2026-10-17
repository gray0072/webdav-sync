<#
.SYNOPSIS
    Builds the portable app (spec 15.1): WebDavSync.exe (tray) and, unless -AppOnly, wds.exe (CLI), single-file, into one folder.

.DESCRIPTION
    Framework-dependent single-file builds (the .NET 10 Desktop Runtime must be installed), win-x64, no .pdb files.
    Both programs use <output>\data\ (settings.json, logs) by default; passwords live in the Windows Credential
    Manager, not in the folder. Copy the folder anywhere writable; the app must not run while it is replaced.

.EXAMPLE
    .\scripts\publish.ps1 -Output D:\Apps\WebDavSync
#>
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\dist'),
    # Release builds ship only the tray app; the CLI is for advanced use and development.
    [switch]$AppOnly
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
New-Item -ItemType Directory -Force $Output | Out-Null
$projects = if ($AppOnly) { @('src\WebDavSync.App') } else { @('src\WebDavSync.App', 'src\WebDavSync.Cli') }
foreach ($project in $projects) {
    dotnet publish (Join-Path $root $project) -c Release -r win-x64 --self-contained false `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $Output
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $project failed" }
}

Get-ChildItem $Output -File | Format-Table Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
