param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [string]$OutputPath = "",
    [string]$NuGetSource = "https://api.nuget.org/v3/index.json",
    [switch]$SelfContained,
    [switch]$IncludeLocalConfig
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repoRoot "dist\ApolloDisconnector"
}

if ((Test-Path $OutputPath) -and -not $IncludeLocalConfig) {
    $packagedConfig = Join-Path $OutputPath "appsettings.json"
    if (Test-Path $packagedConfig) {
        Remove-Item $packagedConfig -Force
    }
}

$publishArgs = @(
    "publish", $repoRoot,
    "-c", $Configuration,
    "-r", $Runtime,
    "--source", $NuGetSource,
    "--self-contained", $SelfContained.IsPresent.ToString().ToLowerInvariant(),
    "-p:PublishSingleFile=true",
    "-p:DebugType=None",
    "-p:DebugSymbols=false",
    "-o", $OutputPath
)

if ($SelfContained) {
    $publishArgs += "-p:PublishReadyToRun=true"
}

dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null

$activeConfig = Join-Path $repoRoot "appsettings.json"
$readme = Join-Path $repoRoot "README.md"
$packagedConfig = Join-Path $OutputPath "appsettings.json"

if ((Test-Path $packagedConfig) -and -not $IncludeLocalConfig) {
    Remove-Item $packagedConfig -Force
}

Copy-Item $readme (Join-Path $OutputPath "README.md") -Force

if ($IncludeLocalConfig -and (Test-Path $activeConfig)) {
    Copy-Item $activeConfig (Join-Path $OutputPath "appsettings.json") -Force
    Write-Host "Copied local appsettings.json into package. Do not share this folder unless credentials are removed."
}

Write-Host "Published to: $OutputPath"
