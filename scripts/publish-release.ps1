param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repoRoot "artifacts\publish\CodePulse"
}

$projectPath = Join-Path $repoRoot "CodePulse.Wpf\CodePulse.Wpf.csproj"

if (Test-Path $OutputPath) {
    Remove-Item -LiteralPath $OutputPath -Recurse -Force
}

dotnet publish $projectPath `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -o $OutputPath `
    /p:PublishSingleFile=false `
    /p:PublishReadyToRun=false

Write-Host "Published CodePulse to $OutputPath"
