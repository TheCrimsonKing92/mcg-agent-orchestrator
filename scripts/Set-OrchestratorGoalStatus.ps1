<#
.SYNOPSIS
  Repair a persisted goal status in .orchestrator/state.db.

.DESCRIPTION
  This is an operator recovery tool for rare state-machine desyncs. It updates the
  indexed goals.status column and the serialized goal snapshot together, resolving
  goal prefixes safely before writing.

  Prefer orchestrator commands such as retry/recover/conduct first. Use this only
  when the persisted status is known to block the conductor from self-recovering.

.EXAMPLE
  .\scripts\Invoke-RepoScript.ps1 scripts\Set-OrchestratorGoalStatus.ps1 --dry-run --status Active f0e13c8c

.EXAMPLE
  .\scripts\Invoke-RepoScript.ps1 scripts\Set-OrchestratorGoalStatus.ps1 --status Active f0e13c8c 17934104
#>
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$toolProject = Join-Path $PSScriptRoot 'OrchestratorSqliteTools\OrchestratorSqliteTools.csproj'

if (-not (Test-Path -LiteralPath $toolProject -PathType Leaf)) {
    throw "SQLite tool project not found: $toolProject"
}

& dotnet run --project $toolProject -- set-goal-status --repo-root $repoRoot @args
exit $LASTEXITCODE
