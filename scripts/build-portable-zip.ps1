[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$Runtime = "win-x64",

    [string]$OutputName = "CodePulse-portable.zip",

    [switch]$SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$publishScript = Join-Path $PSScriptRoot "publish-release.ps1"
$publishDir = Join-Path $repoRoot "artifacts\publish\CodePulse"
$packageDir = Join-Path $repoRoot "artifacts\package"
$zipPath = Join-Path $packageDir $OutputName

if (-not $OutputName.EndsWith(".zip", [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputName must end with .zip"
}

& $publishScript -Configuration $Configuration -Runtime $Runtime -OutputPath $publishDir -SkipTests:$SkipTests

if (-not (Test-Path -LiteralPath $packageDir)) {
    New-Item -ItemType Directory -Path $packageDir | Out-Null
}

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

$exePath = Join-Path $publishDir "CodePulse.Wpf.exe"
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    throw "Cannot create portable package because publish output is missing: $exePath"
}

Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -CompressionLevel Optimal

$zipInfo = Get-Item -LiteralPath $zipPath
if ($zipInfo.Length -le 0) {
    throw "Portable package was created but is empty: $zipPath"
}

Write-Host "Portable package: $zipPath"
