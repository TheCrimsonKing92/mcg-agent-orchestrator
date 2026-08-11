<#
.SYNOPSIS
  Install or remove the opt-in scheduled task that resumes unattended conduct loops.

.DESCRIPTION
  Registers a per-user scheduled task that runs scripts/Resume-OrchestratorLoop.ps1
  at logon and every 10 minutes. This script is opt-in only; nothing installs it
  unless an operator runs this script explicitly.

.EXAMPLE
  pwsh -NoProfile -File scripts/Install-OrchestratorAutoResume.ps1

.EXAMPLE
  pwsh -NoProfile -File scripts/Install-OrchestratorAutoResume.ps1 -Remove
#>
[CmdletBinding()]
param(
    [switch]$Remove,
    [string]$TaskName = "McgOrchestratorAutoResume"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([System.Environment]::OSVersion.Platform -ne [System.PlatformID]::Win32NT) {
    throw "Install-OrchestratorAutoResume.ps1 requires Windows Task Scheduler."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$modulePath = Join-Path $PSScriptRoot "OrchestratorAutoResume.Install.psm1"
Import-Module $modulePath -Force

$localApplicationData = [System.Environment]::GetFolderPath(
    [System.Environment+SpecialFolder]::LocalApplicationData,
    [System.Environment+SpecialFolderOption]::DoNotVerify)
if ([string]::IsNullOrWhiteSpace($localApplicationData)) {
    throw "Windows did not resolve the current user's LocalApplicationData known folder."
}

$currentUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
$operations = New-WindowsAutoResumeOperations -CurrentUser $currentUser

if ($Remove) {
    $result = Invoke-AutoResumeRemoval -RepositoryRoot $repoRoot -LocalApplicationData $localApplicationData -TaskName $TaskName -Operations $operations
    Write-Output "removed task=$TaskName task-was-present=$($result.TaskWasPresent) launcher-root=$($result.RemovedRoot)"
    return
}

$resumeScriptPath = Join-Path $repoRoot "scripts\Resume-OrchestratorLoop.ps1"
if (-not (Test-Path -LiteralPath $resumeScriptPath -PathType Leaf)) {
    throw "Resume script not found: $resumeScriptPath"
}

# pwsh is a console app: routing through the validated WinExe launcher keeps
# resumed operation hidden without elevating the scheduled task.
$launcherProject = Join-Path $repoRoot "tools\Mcg.HiddenLauncher\Mcg.HiddenLauncher.csproj"
$powerShellPath = (Get-Process -Id $PID).Path
$result = Invoke-AutoResumeInstall `
    -RepositoryRoot $repoRoot `
    -LocalApplicationData $localApplicationData `
    -LauncherProject $launcherProject `
    -ResumeScriptPath $resumeScriptPath `
    -PowerShellPath $powerShellPath `
    -TaskName $TaskName `
    -Operations $operations

Write-Output "installed task=$TaskName triggers=logon,repeat-10-minutes script=$resumeScriptPath launcher=$($result.LauncherPath)"
