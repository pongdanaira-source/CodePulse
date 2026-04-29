param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishScript = Join-Path $PSScriptRoot "publish-release.ps1"
$installerScript = Join-Path $repoRoot "installer\CodePulse.iss"

& $publishScript -Configuration $Configuration -Runtime $Runtime

$iscc = Get-Command iscc -ErrorAction SilentlyContinue
if (-not $iscc) {
    $knownPaths = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )

    foreach ($path in $knownPaths) {
        if (Test-Path $path) {
            $iscc = Get-Item $path
            break
        }
    }
}

if (-not $iscc) {
    throw "Inno Setup compiler was not found. Install Inno Setup 6, then run this script again: https://jrsoftware.org/isdl.php"
}

& $iscc.Source $installerScript

Write-Host "Installer output: $(Join-Path $repoRoot 'artifacts\installer')"
