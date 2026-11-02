# Builds the portable app: two self-contained single-file exes in dist\AutoFile, plus a versioned release zip in dist.
# Copy that folder anywhere (USB stick, OneDrive) - no .NET install needed on the target PC.
param(
    [string]$Runtime = "win-x64",
    [string]$Output = (Join-Path $PSScriptRoot "dist\AutoFile")
)

$ErrorActionPreference = "Stop"

$common = @(
    "-c", "Release",
    "-r", $Runtime,
    "--self-contained",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:DebugType=none",
    "-o", $Output
)

foreach ($project in "src\AutoFile.Cli", "src\AutoFile.App") {
    dotnet publish (Join-Path $PSScriptRoot $project) @common
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# Release zip: just the exes and the README, never the AutoFile.data folder a local run may have left beside them.
$version = (dotnet msbuild (Join-Path $PSScriptRoot "src\AutoFile.App") -getProperty:Version).Trim()
$zip = Join-Path (Split-Path $Output) "AutoFile-$version-$Runtime.zip"
$files = "AutoFileApp.exe", "autofile.exe" | ForEach-Object { Join-Path $Output $_ }
Compress-Archive -Path ($files + (Join-Path $PSScriptRoot "README.md")) -DestinationPath $zip -Force

Write-Host "`nPortable app is in $Output"
Write-Host "Release zip is $zip"
