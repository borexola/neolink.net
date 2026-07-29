# Builds Neolink.NET Desktop and packages it as an MSI.
#
#   pwsh installer/build-msi.ps1                 # version from the csproj
#   pwsh installer/build-msi.ps1 -Version 0.9.9  # or forced (CI passes the tag)
#
# Needs the WiX v5 CLI and its two extensions:
#   dotnet tool install --global wix --version 5.0.2
#   wix extension add -g WixToolset.Util.wixext/5.0.2
#   wix extension add -g WixToolset.UI.wixext/5.0.2
#
# WiX v5 rather than v6/v7 on purpose: from v6 the toolset requires accepting the
# Open Source Maintenance Fee EULA, which is a licensing decision for the project
# owner, not a build detail. v5 is plain open source and does everything needed.
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [string]$OutputDir
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $repo "src/Neolink.Desktop/Neolink.Desktop.csproj"
# Built from $repo, not from a relative string: a "..\" inside an unresolved path
# is resolved by whoever happens to expand it, and that is not always the shell.
if (-not $OutputDir) { $OutputDir = Join-Path $repo "artifacts" }

if (-not $Version) {
    $csproj = [xml](Get-Content $project)
    $Version = ($csproj.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1
}
# An MSI ProductVersion is numeric only: 0.9.8-beta.3 installs as 0.9.8. The app
# still reports its own full version; this is only what Add/Remove Programs sees.
$msiVersion = ($Version -split '-')[0]
if ($msiVersion -notmatch '^\d+(\.\d+){0,3}$') {
    throw "Version '$Version' does not reduce to a numeric MSI version (got '$msiVersion')"
}

Write-Host "Neolink.NET Desktop $Version  ($Runtime, MSI version $msiVersion)" -ForegroundColor Cyan

# ---- publish ---------------------------------------------------------------
$publish = Join-Path ([System.IO.Path]::GetTempPath()) "neolink-desktop-publish-$Runtime"
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }

Write-Host "publishing..." -ForegroundColor DarkGray
& dotnet publish $project -c $Configuration -r $Runtime -o $publish --nologo `
    -p:Version=$Version | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$exe = Join-Path $publish "Neolink.Desktop.exe"
if (-not (Test-Path $exe)) { throw "publish produced no Neolink.Desktop.exe" }
$sizeMb = [math]::Round(((Get-ChildItem $publish -Recurse | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host "  $publish  ($sizeMb MB)" -ForegroundColor DarkGray

# ---- licence, as RTF for the installer's licence page ----------------------
# The MSI UI only renders RTF, and the repository keeps the AGPL as plain text;
# wrap it rather than committing a second copy that could drift.
$licenseRtf = Join-Path $publish "license.rtf"
$licenseText = Get-Content "$repo/LICENSE" -Raw
$escaped = $licenseText -replace '\\', '\\\\' -replace '\{', '\{' -replace '\}', '\}'
$escaped = $escaped -replace "`r`n", '\par ' -replace "`n", '\par '
"{\rtf1\ansi\deff0{\fonttbl{\f0\fnil\fcharset0 Segoe UI;}}\fs16 $escaped}" |
    Set-Content -Path $licenseRtf -Encoding ascii

# ---- package ---------------------------------------------------------------
New-Item -ItemType Directory -Force $OutputDir | Out-Null
$msi = Join-Path (Resolve-Path $OutputDir) "Neolink.NET.Desktop-$Version-$Runtime.msi"

$arch = switch ($Runtime) {
    "win-x64"   { "x64" }
    "win-arm64" { "arm64" }
    "win-x86"   { "x86" }
    default     { throw "unsupported runtime '$Runtime'" }
}

Write-Host "packaging..." -ForegroundColor DarkGray
& wix build "$PSScriptRoot/Neolink.Desktop.wxs" `
    -arch $arch `
    -d "Version=$msiVersion" `
    -d "PublishDir=$publish" `
    -d "LicenseRtf=$licenseRtf" `
    -ext WixToolset.Util.wixext `
    -ext WixToolset.UI.wixext `
    -o $msi
if ($LASTEXITCODE -ne 0) { throw "wix build failed" }

$msiMb = [math]::Round(((Get-Item $msi).Length / 1MB), 1)
Write-Host "`n$msi  ($msiMb MB)" -ForegroundColor Green
