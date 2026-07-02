<#
.SYNOPSIS
  Run a PowerShell script that lives inside this repository.

.DESCRIPTION
  Codex command allowlists work best with stable command prefixes. Ad-hoc PowerShell
  one-liners and direct `powershell -File <path>` calls often trigger repeated
  permission prompts because the meaningful operation is hidden behind a broad shell.

  This wrapper is intentionally narrow: it resolves the target path against the
  repository root, requires a `.ps1` file, and refuses to run anything outside the
  checkout. Approve this wrapper prefix once to run checked-in repo scripts without
  approving arbitrary PowerShell.

.EXAMPLE
  .\scripts\Invoke-RepoScript.ps1 scripts\Find-OrchestratorLocks.ps1

.EXAMPLE
  .\scripts\Invoke-RepoScript.ps1 scripts\Invoke-IsolatedDotnet.ps1 -GoalPrefix abc12345 test Mcg.AgentOrchestrator.sln --verbosity minimal
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$ScriptPath,

    [Parameter(ValueFromRemainingArguments = $true)]
    [object[]]$ScriptArguments
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ScriptPath)) {
    throw "Usage: .\scripts\Invoke-RepoScript.ps1 <repo-relative-script.ps1> [script arguments...]"
}

$ScriptArguments = if ($null -eq $ScriptArguments -or $ScriptArguments.Count -eq 0) {
    @()
} else {
    @($ScriptArguments | ForEach-Object { [string]$_ })
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$candidate = if ([System.IO.Path]::IsPathRooted($ScriptPath)) {
    $ScriptPath
} else {
    Join-Path $repoRoot $ScriptPath
}

$resolved = [System.IO.Path]::GetFullPath($candidate)
$rootWithSeparator = $repoRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

if (-not ($resolved.StartsWith($rootWithSeparator, [System.StringComparison]::OrdinalIgnoreCase) -or
          [string]::Equals($resolved, $repoRoot, [System.StringComparison]::OrdinalIgnoreCase))) {
    throw "Refusing to run script outside repository root: $resolved"
}

if ([System.IO.Path]::GetExtension($resolved) -ne '.ps1') {
    throw "Refusing to run non-PowerShell script: $resolved"
}

if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
    throw "Script not found: $resolved"
}

$powerShellPath = (Get-Process -Id $PID).Path
& $powerShellPath -NoProfile -ExecutionPolicy Bypass -File $resolved @ScriptArguments
if ($LASTEXITCODE -is [int]) {
    exit $LASTEXITCODE
}
