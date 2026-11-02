# Builds the portable app: two self-contained single-file exes in dist\AutoFile.
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

Write-Host "`nPortable app is in $Output"
