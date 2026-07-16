<#
.SYNOPSIS
  Resume the last journaled unattended conduct loop when no loop is running.

.DESCRIPTION
  Idempotent reboot/crash recovery for operator drives. The script no-ops when a
  conduct loop is already running or when .conduct-stop is present, otherwise it
  relaunches scripts/Start-OrchestratorCommand.ps1 from .orchestrator/last-drive.json.

.EXAMPLE
  pwsh -NoProfile -File scripts/Resume-OrchestratorLoop.ps1
#>
[CmdletBinding()]
param(
    [string]$JournalPath,
    [string]$TaskName = "McgOrchestratorAutoResume"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-JournalPath {
    param([string]$RepositoryRoot, [string]$ConfiguredPath)

    if ([string]::IsNullOrWhiteSpace($ConfiguredPath)) {
        return Join-Path $RepositoryRoot ".orchestrator\last-drive.json"
    }

    return [System.IO.Path]::GetFullPath($ConfiguredPath)
}

function Resolve-NextBatchName {
    param([string]$CurrentName)

    if ([string]::IsNullOrWhiteSpace($CurrentName)) {
        $CurrentName = "conduct-loop"
    }

    $match = [System.Text.RegularExpressions.Regex]::Match(
        $CurrentName,
        "^(?<prefix>.*?)(?<number>\d+)$",
        [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $match.Success) {
        return "$CurrentName-handoff-1"
    }

    $digits = $match.Groups["number"].Value
    $next = [long]::Parse($digits, [System.Globalization.CultureInfo]::InvariantCulture) + 1
    return $match.Groups["prefix"].Value + $next.ToString(("0" * $digits.Length), [System.Globalization.CultureInfo]::InvariantCulture)
}

function Test-ConductLoopRunning {
    param([string]$RepositoryRoot)

    $helperPath = Join-Path $RepositoryRoot "scripts\Get-RepoProcessInfo.ps1"
    if (-not (Test-Path -LiteralPath $helperPath -PathType Leaf)) {
        throw "Repo process helper not found: $helperPath"
    }

    $powerShellPath = (Get-Process -Id $PID).Path
    if ([string]::IsNullOrWhiteSpace($powerShellPath)) {
        $powerShellPath = "powershell.exe"
    }

    $output = @(& $powerShellPath -NoProfile -ExecutionPolicy Bypass -File $helperPath -ConductLoop -Newest 10 2>&1)
    $helperExitCode = $LASTEXITCODE
    if ($helperExitCode -is [int] -and $helperExitCode -ne 0) {
        throw "Repo process helper failed with exit code $helperExitCode`: $($output -join ' ')"
    }

    foreach ($line in $output) {
        if ([string]$line -match "^PROCESS\s+" -and [string]$line -notmatch "status=missing") {
            return $true
        }
    }

    return $false
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$stopFilePath = Join-Path $repoRoot ".conduct-stop"
$resolvedJournalPath = Resolve-JournalPath -RepositoryRoot $repoRoot -ConfiguredPath $JournalPath

if (Test-Path -LiteralPath $stopFilePath -PathType Leaf) {
    Write-Output "RESUME_SKIPPED reason=conduct-stop path=$stopFilePath"
    exit 0
}

try {
    if (Test-ConductLoopRunning -RepositoryRoot $repoRoot) {
        Write-Output "RESUME_SKIPPED reason=conduct-loop-running"
        exit 0
    }
}
catch {
    Write-Output "RESUME_SKIPPED reason=process-query-unavailable detail=$($_.Exception.Message)"
    exit 1
}

if (-not (Test-Path -LiteralPath $resolvedJournalPath -PathType Leaf)) {
    Write-Output "RESUME_SKIPPED reason=journal-missing path=$resolvedJournalPath"
    exit 0
}

$journal = Get-Content -LiteralPath $resolvedJournalPath -Raw | ConvertFrom-Json
$arguments = @($journal.arguments | ForEach-Object { [string]$_ })
if ($arguments.Count -lt 2 -or
    -not $arguments[0].Equals("conduct", [System.StringComparison]::OrdinalIgnoreCase) -or
    -not ($arguments | Where-Object { $_.Equals("--loop", [System.StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1)) {
    Write-Output "RESUME_SKIPPED reason=journal-not-conduct-loop path=$resolvedJournalPath"
    exit 0
}

$nextName = Resolve-NextBatchName -CurrentName ([string]$journal.name)
$startScriptPath = Join-Path $repoRoot "scripts\Start-OrchestratorCommand.ps1"
if ($null -ne $journal.appDll -and -not [string]::IsNullOrWhiteSpace([string]$journal.appDll)) {
    Write-Output "RESUME_LAUNCH task=$TaskName journal=$resolvedJournalPath name=$nextName"
    & $startScriptPath -Name $nextName -AppDll ([string]$journal.appDll) @arguments
}
else {
    Write-Output "RESUME_LAUNCH task=$TaskName journal=$resolvedJournalPath name=$nextName"
    & $startScriptPath -Name $nextName @arguments
}
if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
