[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$Runtime = "win-x64",

    [switch]$SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$publishScript = Join-Path $PSScriptRoot "publish-release.ps1"
$installerScript = Join-Path $repoRoot "installer\CodePulse.iss"
$installerOutputDir = Join-Path $repoRoot "artifacts\installer"

if (-not (Test-Path -LiteralPath $installerScript -PathType Leaf)) {
    throw "Inno Setup script was not found: $installerScript"
}

$installerScriptText = Get-Content -LiteralPath $installerScript -Raw
$versionMatch = [regex]::Match($installerScriptText, '#define\s+MyAppVersion\s+"(?<version>[^"]+)"')
if (-not $versionMatch.Success) {
    throw "Cannot determine installer version from: $installerScript"
}

$installerVersion = $versionMatch.Groups["version"].Value

& $publishScript -Configuration $Configuration -Runtime $Runtime -SkipTests:$SkipTests

$iscc = Get-Command iscc -ErrorAction SilentlyContinue
$isccPath = if ($iscc) { $iscc.Source } else { "" }
if ([string]::IsNullOrWhiteSpace($isccPath)) {
    $knownPaths = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )

    foreach ($path in $knownPaths) {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $isccPath = (Get-Item -LiteralPath $path).FullName
            break
        }
    }
}

if ([string]::IsNullOrWhiteSpace($isccPath)) {
    throw "Inno Setup compiler was not found. Install Inno Setup 6, then run this script again: winget install JRSoftware.InnoSetup"
}

if (-not (Test-Path -LiteralPath $installerOutputDir)) {
    New-Item -ItemType Directory -Path $installerOutputDir | Out-Null
}

Write-Host "Building installer with $isccPath..."
& $isccPath $installerScript

$expectedInstaller = Join-Path $installerOutputDir "CodePulse-Setup-$installerVersion.exe"
if (-not (Test-Path -LiteralPath $expectedInstaller -PathType Leaf)) {
    throw "Installer compiler completed, but expected output was not found: $expectedInstaller"
}

Write-Host "Installer output: $installerOutputDir"
