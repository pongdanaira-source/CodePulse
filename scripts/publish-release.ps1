[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$Runtime = "win-x64",

    [string]$OutputPath = "",

    [switch]$SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$solutionPath = Join-Path $repoRoot "CodePulse.Wpf\CodePulse.Wpf.sln"
$projectPath = Join-Path $repoRoot "CodePulse.Wpf\CodePulse.Wpf.csproj"
$artifactsRoot = Join-Path $repoRoot "artifacts"

function Resolve-FromRepo {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $repoRoot $Path))
}

function Assert-ExistingFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required file was not found: $Path"
    }
}

function Assert-SafeDeleteDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    $repoPath = [System.IO.Path]::GetFullPath($repoRoot).TrimEnd('\')
    $artifactsPath = [System.IO.Path]::GetFullPath($artifactsRoot).TrimEnd('\')
    $driveRoot = [System.IO.Path]::GetPathRoot($fullPath).TrimEnd('\')

    if ($fullPath -eq $driveRoot -or $fullPath -eq $repoPath -or $fullPath -eq $artifactsPath) {
        throw "Refusing to delete unsafe directory: $fullPath"
    }

    $artifactsPrefix = $artifactsPath + "\"
    if (-not $fullPath.StartsWith($artifactsPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Publish output must be inside artifacts for automatic cleanup: $fullPath"
    }
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = "artifacts\publish\CodePulse"
}

$outputFullPath = Resolve-FromRepo $OutputPath

Assert-ExistingFile $solutionPath
Assert-ExistingFile $projectPath

if (-not $SkipTests) {
    Write-Host "Running tests before publish..."
    dotnet test $solutionPath -c $Configuration --nologo
}
else {
    Write-Host "Skipping tests before publish."
}

if (Test-Path -LiteralPath $outputFullPath) {
    Assert-SafeDeleteDirectory $outputFullPath
    Remove-Item -LiteralPath $outputFullPath -Recurse -Force
}

New-Item -ItemType Directory -Path $outputFullPath -Force | Out-Null

Write-Host "Publishing CodePulse ($Configuration, $Runtime) to $outputFullPath..."
dotnet publish $projectPath `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -o $outputFullPath `
    /p:PublishSingleFile=false `
    /p:PublishReadyToRun=false

$exePath = Join-Path $outputFullPath "CodePulse.Wpf.exe"
Assert-ExistingFile $exePath

$forbiddenOutputs = @()
$settingsFile = Join-Path $outputFullPath "settings.json"
if (Test-Path -LiteralPath $settingsFile) {
    $forbiddenOutputs += $settingsFile
}

$runtimeDebug = Join-Path $outputFullPath "runtime-debug"
if (Test-Path -LiteralPath $runtimeDebug) {
    $forbiddenOutputs += $runtimeDebug
}

if ($forbiddenOutputs.Count -gt 0) {
    $joined = $forbiddenOutputs -join ", "
    throw "Publish output contains local runtime data or settings: $joined"
}

Write-Host "Published CodePulse to $outputFullPath"
