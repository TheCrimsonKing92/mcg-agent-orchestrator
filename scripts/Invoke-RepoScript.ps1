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

if ([string]::Equals([System.IO.Path]::GetFileName($resolved), 'Invoke-Git.ps1', [System.StringComparison]::OrdinalIgnoreCase)) {
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
                $scriptArgumentOffset = $i + 2
                break
            }
        } catch {
        }
    }

    $gitArguments = if ($scriptArgumentOffset -ge 0 -and $scriptArgumentOffset -lt $processArguments.Count) {
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
                    $lineScriptOffset = $i + 2
                    break
                }
            } catch {
            }
        }

        if ($lineScriptOffset -ge 0 -and $lineScriptOffset -lt $lineTokens.Count) {
            @($lineTokens[$lineScriptOffset..($lineTokens.Count - 1)] | ForEach-Object { $_.Content })
        } else {
            @($ScriptArguments)
        }
    }

    & git -C $repoRoot @gitArguments
    exit $LASTEXITCODE
}

$powerShellPath = (Get-Process -Id $PID).Path
& $powerShellPath -NoProfile -ExecutionPolicy Bypass -File $resolved @ScriptArguments
if ($LASTEXITCODE -is [int]) {
    exit $LASTEXITCODE
}
