<#
.SYNOPSIS
  Wait for a bounded interval from a repo-approved script prefix.

.DESCRIPTION
  This is a small wrapper around Start-Sleep for operator workflows where ad-hoc
  PowerShell one-liners would trigger repeated permission prompts. Invoke it
  through scripts/Invoke-RepoScript.ps1.
#>
param(
    [ValidateRange(1, 600)]
    [int]$Seconds = 60,

    [string]$Label = ''
)

$ErrorActionPreference = 'Stop'

if (-not [string]::IsNullOrWhiteSpace($Label)) {
    Write-Output "WAIT_START seconds=$Seconds label=$Label"
}

Start-Sleep -Seconds $Seconds

if (-not [string]::IsNullOrWhiteSpace($Label)) {
    Write-Output "WAIT_END seconds=$Seconds label=$Label"
}
