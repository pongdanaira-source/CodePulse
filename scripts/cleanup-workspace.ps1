[CmdletBinding()]
param(
    [switch]$Apply,
    [switch]$IncludeArtifacts,
    [switch]$IncludeBuildOutputs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$targets = New-Object System.Collections.Generic.List[System.IO.DirectoryInfo]

function Add-CleanupTarget {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (Test-Path -LiteralPath $Path -PathType Container) {
        $targets.Add((Get-Item -LiteralPath $Path))
    }
}

function Add-CleanupTargetsByFilter {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Filter
    )

    if (Test-Path -LiteralPath $Path -PathType Container) {
        Get-ChildItem -LiteralPath $Path -Directory -Filter $Filter -ErrorAction SilentlyContinue |
            ForEach-Object { $targets.Add($_) }
    }
}

function Assert-SafeDeleteDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    $repoPath = [System.IO.Path]::GetFullPath($repoRoot).TrimEnd('\')
    $driveRoot = [System.IO.Path]::GetPathRoot($fullPath).TrimEnd('\')

    if ($fullPath -eq $driveRoot -or $fullPath -eq $repoPath) {
        throw "Refusing to delete unsafe directory: $fullPath"
    }

    $repoPrefix = $repoPath + "\"
    if (-not $fullPath.StartsWith($repoPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Cleanup target must be inside the repository: $fullPath"
    }
}

Add-CleanupTargetsByFilter -Path $repoRoot -Filter "_tmpbuild_verify_*"
Add-CleanupTargetsByFilter -Path (Join-Path $repoRoot "CodePulse.Wpf") -Filter "_tmpbuild_verify_*"
Add-CleanupTarget -Path (Join-Path $repoRoot "_tmp_resolve_check")

if ($IncludeBuildOutputs) {
    $projectDirs = @(
        Join-Path $repoRoot "CodePulse",
        Join-Path $repoRoot "CodePulse.Wpf",
        Join-Path $repoRoot "CodePulse.Tests"
    )

    foreach ($projectDir in $projectDirs) {
        Add-CleanupTarget -Path (Join-Path $projectDir "bin")
        Add-CleanupTarget -Path (Join-Path $projectDir "obj")
    }
}

if ($IncludeArtifacts) {
    $artifacts = Join-Path $repoRoot "artifacts"
    Add-CleanupTarget -Path (Join-Path $artifacts "publish")
    Add-CleanupTarget -Path (Join-Path $artifacts "package")
    Add-CleanupTarget -Path (Join-Path $artifacts "installer")
}

$uniqueTargets = @(
    $targets |
        Where-Object { $_ -ne $null } |
        Sort-Object FullName -Unique
)

if ($uniqueTargets.Count -eq 0) {
    Write-Host "No cleanup targets found."
    exit 0
}

Write-Host "Cleanup targets:"
foreach ($target in $uniqueTargets) {
    Assert-SafeDeleteDirectory $target.FullName
    Write-Host " - $($target.FullName)"
}

if (-not $Apply) {
    Write-Host ""
    Write-Host "Preview only. Re-run with -Apply to delete these folders."
    exit 0
}

foreach ($target in $uniqueTargets) {
    Remove-Item -LiteralPath $target.FullName -Recurse -Force
}

Write-Host "Cleanup complete."
