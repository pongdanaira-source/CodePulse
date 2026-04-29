param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishScript = Join-Path $PSScriptRoot "publish-release.ps1"
$publishDir = Join-Path $repoRoot "artifacts\publish\CodePulse"
$packageDir = Join-Path $repoRoot "artifacts\package"
$zipPath = Join-Path $packageDir "CodePulse-portable.zip"

& $publishScript -Configuration $Configuration -Runtime $Runtime -OutputPath $publishDir

if (-not (Test-Path $packageDir)) {
    New-Item -ItemType Directory -Path $packageDir | Out-Null
}

if (Test-Path $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host "Portable package: $zipPath"
