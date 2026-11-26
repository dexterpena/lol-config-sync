<#
.SYNOPSIS
  Builds the release downloads into .\dist:
    LolConfigSync-Setup-<version>.exe     installer (per-user, Start menu shortcut, uninstaller)
    LolConfigSync-<version>-portable.zip  extract anywhere; data stays in the extracted folder

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build\package.ps1
#>
param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist 'stage'
$project = Join-Path $root 'src\LolConfigSync.App\LolConfigSync.App.csproj'

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = 'C:\Program Files\dotnet\dotnet.exe' }

[xml]$csproj = Get-Content $project
$version = ($csproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw "No <Version> in $project" }
Write-Host "Packaging LoL Config Sync $version"

if (-not $SkipTests) {
  & $dotnet test $root --nologo
  if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
}

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
& $dotnet publish $project -c Release -o $stage --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
$exe = Join-Path $stage 'LolConfigSync.exe'
if (-not (Test-Path $exe)) { throw "Publish did not produce $exe" }

# Portable zip: a single "LoL Config Sync" folder so extracting never scatters files.
$portableRoot = Join-Path $dist 'portable\LoL Config Sync'
New-Item -ItemType Directory -Force $portableRoot | Out-Null
Copy-Item $exe $portableRoot
@"
LoL Config Sync $version (portable)

Run LolConfigSync.exe. Your setup, settings and backups are saved in the "data"
folder next to it, so you can move or copy this whole folder and they come along.

To remove it: quit from the tray icon, untick "Start with Windows" first if you
turned it on, then delete this folder.
"@ | Set-Content (Join-Path $portableRoot 'README.txt') -Encoding utf8
$zip = Join-Path $dist "LolConfigSync-$version-portable.zip"
Compress-Archive -Path $portableRoot -DestinationPath $zip -CompressionLevel Optimal

# Installer.
$iscc = @(
  "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
  "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
  "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup' }
& $iscc /Qp "/DAppVersion=$version" "/DPublishDir=$stage" (Join-Path $root 'installer\LolConfigSync.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed' }

Remove-Item (Join-Path $dist 'portable') -Recurse -Force
Write-Host ''
Get-ChildItem $dist -File | ForEach-Object { '{0,-40} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) }
