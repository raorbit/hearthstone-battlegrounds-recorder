# Builds the Velopack installer: dotnet publish (self-contained x64) -> vpk pack.
#
# Prereqs: .NET 10 SDK, Node (the publish builds the Vite bundle), and the vpk tool:
#   dotnet tool install --global vpk
#
# Usage: powershell -File scripts\pack.ps1 -Version 0.1.0
# Output: artifacts\releases\BgRecorder-win-Setup.exe (+ the full/delta packages and RELEASES feed).
#
# Unsigned by design for v1 — SmartScreen will warn on first run (documented caveat; a code-signing
# cert is out of scope, see docs/implementation-plan.md "Out of scope for v1").
param(
    [Parameter(Mandatory = $true)][string]$Version
)

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$publishDir = Join-Path $repo "artifacts\publish"
$releaseDir = Join-Path $repo "artifacts\releases"

if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
# Fresh output every run: vpk (correctly) refuses to pack a version equal to one already in the
# feed dir, which would make same-version local rebuilds fail. Delta-update feeds are a later
# concern; when they arrive, the previous releases dir must be KEPT instead.
if (Test-Path $releaseDir) { Remove-Item -Recurse -Force $releaseDir }

# Self-contained so a clean machine needs no .NET install; Velopack manages the app dir, so no
# single-file bundling. The publish also builds and copies the Web bundle (csproj targets).
# -p:Version stamps the assemblies too — otherwise the startup log and file properties would say
# 1.0.0.0 forever and releases couldn't be correlated to logs.
dotnet publish (Join-Path $repo "src\BgRecorder.App\BgRecorder.App.csproj") `
    -c Release -r win-x64 --self-contained -o $publishDir -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# License obligations travel with the binaries.
Copy-Item (Join-Path $repo "THIRD-PARTY-NOTICES.md") $publishDir
Copy-Item (Join-Path $repo "LICENSE") $publishDir

vpk pack `
    --packId BgRecorder `
    --packVersion $Version `
    --packDir $publishDir `
    --mainExe BgRecorder.App.exe `
    --packTitle "BG Recorder" `
    --packAuthors raorbit `
    --outputDir $releaseDir
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

Write-Host "`nInstaller ready under $releaseDir" -ForegroundColor Green
