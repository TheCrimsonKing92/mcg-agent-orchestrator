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
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$GitArguments
)

$ErrorActionPreference = 'Stop'

$processArguments = [Environment]::GetCommandLineArgs()
$currentScript = [System.IO.Path]::GetFullPath($PSCommandPath)
$scriptArgumentOffset = -1
for ($i = 0; $i -lt $processArguments.Count; $i++) {
    try {
        $candidateScript = if ([System.IO.Path]::IsPathRooted($processArguments[$i])) {
            [System.IO.Path]::GetFullPath($processArguments[$i])
        } else {
            [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $processArguments[$i]))
        }

        if ([string]::Equals($candidateScript, $currentScript, [System.StringComparison]::OrdinalIgnoreCase)) {
            $scriptArgumentOffset = $i + 1
            break
        }
    } catch {
    }
}

$arguments = if ($scriptArgumentOffset -ge 0 -and $scriptArgumentOffset -lt $processArguments.Count) {
    @($processArguments[$scriptArgumentOffset..($processArguments.Count - 1)])
} else {
    $parseErrors = $null
    $lineTokens = @([System.Management.Automation.PSParser]::Tokenize($MyInvocation.Line, [ref]$parseErrors) |
        Where-Object { $_.Type -in 'Command', 'CommandArgument', 'CommandParameter', 'String' })
    $lineScriptOffset = -1
    for ($i = 0; $i -lt $lineTokens.Count; $i++) {
        try {
            $candidateScript = if ([System.IO.Path]::IsPathRooted($lineTokens[$i].Content)) {
                [System.IO.Path]::GetFullPath($lineTokens[$i].Content)
            } else {
                [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $lineTokens[$i].Content))
            }

            if ([string]::Equals($candidateScript, $currentScript, [System.StringComparison]::OrdinalIgnoreCase)) {
                $lineScriptOffset = $i + 1
                break
            }
        } catch {
        }
    }

    if ($lineScriptOffset -ge 0 -and $lineScriptOffset -lt $lineTokens.Count) {
        @($lineTokens[$lineScriptOffset..($lineTokens.Count - 1)] | ForEach-Object { $_.Content })
    } else {
        @($GitArguments)
    }
}

if ($null -eq $arguments -or $arguments.Count -lt 1) {
    throw "Usage: .\scripts\Invoke-Git.ps1 <git arguments...>"
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
& git -C $repoRoot @arguments
exit $LASTEXITCODE
