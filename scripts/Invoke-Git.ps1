<#
.SYNOPSIS
  Run git from the repository root through a stable allowlist-friendly script prefix.

.DESCRIPTION
  This wrapper gives Codex a stable checked-in command for repo-local git operations when
  broad `git` approvals are not available. It always runs with `-C <repo-root>` so callers
  get repository-relative behavior regardless of the current working directory.

  Safety remains an operator/agent rule: do not use destructive git operations such as
  reset/clean/forced checkout unless the user explicitly asks for them.

.EXAMPLE
  .\scripts\Invoke-RepoScript.ps1 scripts\Invoke-Git.ps1 status --short

.EXAMPLE
  .\scripts\Invoke-RepoScript.ps1 scripts\Invoke-Git.ps1 diff --stat
#>
$ErrorActionPreference = 'Stop'

if ($args.Count -lt 1) {
    throw "Usage: .\scripts\Invoke-Git.ps1 <git arguments...>"
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
& git -C $repoRoot @args
exit $LASTEXITCODE
