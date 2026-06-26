#requires -Version 5
<#
.SYNOPSIS
  Wrapper around SysInternals handle64 to identify which process holds a handle/lock on a
  file or directory path. The companion to Find-OrchestratorLocks.ps1: that one finds App.dll
  build-lock holders by NAME; this one finds the holder of ANY path by HANDLE.

.DESCRIPTION
  Locates handle64 (env override -> PATH -> common SysInternals install dirs), then runs
  `handle64 -accepteula -nobanner <path>` and prints the holding process name(s) + PID(s).
  Allowlisted as PowerShell(.\scripts\Find-HandleLock.ps1*) so it never prompts.

  Typical use: a build fails CS2012 / MSB3491 "being used by another process", or a slot
  artifact / state.db is wedged. Run:  .\scripts\Find-HandleLock.ps1 <locked-path-or-substring>

.PARAMETER Path
  A full path or a path SUBSTRING to query (handle64 matches substrings).

.EXAMPLE
  .\scripts\Find-HandleLock.ps1 "Mcg.AgentOrchestrator.Core.dll"
.EXAMPLE
  .\scripts\Find-HandleLock.ps1 ".orchestrator-worktrees\d5aed2cd"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Path
)

$ErrorActionPreference = 'Stop'

function Resolve-Handle64 {
    # 1. Explicit override.
    if ($env:MCG_HANDLE64 -and (Test-Path -LiteralPath $env:MCG_HANDLE64 -PathType Leaf)) {
        return $env:MCG_HANDLE64
    }
    # 2. On PATH.
    $cmd = Get-Command 'handle64.exe', 'handle.exe' -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($cmd) { return $cmd.Source }
    # 3. Common SysInternals install dirs (non-recursive; bounded, no whole-disk scan).
    $dirs = @(
        "$env:USERPROFILE\Downloads",
        "$env:USERPROFILE\Downloads\Handle",
        "$env:USERPROFILE\Downloads\SysinternalsSuite",
        "$env:USERPROFILE\Desktop",
        $env:TEMP,
        'C:\Sysinternals',
        'C:\SysinternalsSuite',
        'C:\Tools',
        'C:\Tools\Sysinternals',
        "$env:ChocolateyInstall\bin",
        'C:\ProgramData\chocolatey\bin',
        "$env:USERPROFILE\scoop\shims"
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Container) }
    foreach ($d in $dirs) {
        foreach ($name in @('handle64.exe', 'handle.exe')) {
            $hit = Get-ChildItem -LiteralPath $d -Filter $name -File -ErrorAction SilentlyContinue |
                Select-Object -First 1
            if ($hit) { return $hit.FullName }
        }
    }
    return $null
}

$handle = Resolve-Handle64
if (-not $handle) {
    Write-Output "handle64 not found. Set MCG_HANDLE64 to its full path, or drop handle64.exe in Downloads / C:\Sysinternals / C:\Tools / on PATH."
    exit 2
}

Write-Output "handle64: $handle"
Write-Output "query:    $Path"
Write-Output '---'
# handle64 takes the search fragment as a bare positional arg (no GNU -- separator).
& $handle -accepteula -nobanner $Path
$code = $LASTEXITCODE
if ($code -ne 0) {
    Write-Output "(no open handles matched '$Path', or handle64 needs elevation for this holder)"
}
exit 0
