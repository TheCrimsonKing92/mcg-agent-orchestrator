<#
.SYNOPSIS
  Print a bounded line window from a file inside this repository.

.DESCRIPTION
  Use this through scripts\Invoke-RepoScript.ps1 when Codex needs a narrow source
  slice without ad-hoc PowerShell pipelines such as Get-Content | Select-Object.
  The target path is resolved against the repository root and must stay inside it.

.EXAMPLE
  .\scripts\Invoke-RepoScript.ps1 scripts\Show-RepoFileSlice.ps1 src\Example.cs 40 25
#>
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Path,

    [Parameter(Position = 1)]
    [ValidateRange(1, 2147483647)]
    [int]$Start = 1,

    [Parameter(Position = 2)]
    [ValidateRange(1, 500)]
    [int]$Count = 80
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$candidate = if ([System.IO.Path]::IsPathRooted($Path)) {
    $Path
} else {
    Join-Path $repoRoot $Path
}

$resolved = [System.IO.Path]::GetFullPath($candidate)
$rootWithSeparator = $repoRoot.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

if (-not ($resolved.StartsWith($rootWithSeparator, [System.StringComparison]::OrdinalIgnoreCase) -or
          [string]::Equals($resolved, $repoRoot, [System.StringComparison]::OrdinalIgnoreCase))) {
    throw "Refusing to read file outside repository root: $resolved"
}

if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
    throw "File not found: $resolved"
}

$end = $Start + $Count - 1
$lineNumber = 0
foreach ($line in [System.IO.File]::ReadLines($resolved)) {
    $lineNumber++
    if ($lineNumber -lt $Start) {
        continue
    }

    if ($lineNumber -gt $end) {
        break
    }

    "{0,5}: {1}" -f $lineNumber, $line
}
