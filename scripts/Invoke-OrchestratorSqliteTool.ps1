<#
.SYNOPSIS
  Run the repository SQLite utility through the stable repo script wrapper.

.DESCRIPTION
  This keeps operator reads and repairs on the same allowlist-friendly path as the
  other repo helpers. Prefer:

    .\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorSqliteTool.ps1 ...

  over ad-hoc `dotnet run --project ...` commands in Codex.
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Arguments = @($args | ForEach-Object { [string]$_ })

if ($Arguments.Count -eq 0) {
    throw "Usage: .\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorSqliteTool.ps1 <sqlite-tool-args...>"
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "scripts\OrchestratorSqliteTools"
$toolCommand = $Arguments[0]
$toolArguments = @()
if ($Arguments.Count -gt 1) {
    $toolArguments = $Arguments[1..($Arguments.Count - 1)]
}

$dotnetArguments = @("run", "--project", $projectPath, "--", $toolCommand) + $toolArguments
& dotnet @dotnetArguments
if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
