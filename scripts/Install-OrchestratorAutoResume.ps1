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

function Quote-TaskArgument {
    param([string]$Value)

    '"' + $Value.Replace('"', '\"') + '"'
}

if ([System.Environment]::OSVersion.Platform -ne [System.PlatformID]::Win32NT) {
    throw "Install-OrchestratorAutoResume.ps1 requires Windows Task Scheduler."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$resumeScriptPath = Join-Path $repoRoot "scripts\Resume-OrchestratorLoop.ps1"
if (-not (Test-Path -LiteralPath $resumeScriptPath -PathType Leaf)) {
    throw "Resume script not found: $resumeScriptPath"
}

if ($Remove) {
    $existingTask = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($null -eq $existingTask) {
        Write-Output "removed task=$TaskName status=missing"
        return
    }

    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction Stop
    Write-Output "removed task=$TaskName"
    return
}

$powerShellPath = (Get-Process -Id $PID).Path

# pwsh is a console app: even with -WindowStyle Hidden, conhost flashes a
# window before the flag is honored. Routing the launch through the WinExe
# hidden launcher never creates a console at all.
$launcherProject = Join-Path $repoRoot "tools\Mcg.HiddenLauncher\Mcg.HiddenLauncher.csproj"
$launcherPublishDirectory = Join-Path $repoRoot ".orchestrator\tools\hidden-launcher"
$launcherPath = Join-Path $launcherPublishDirectory "Mcg.HiddenLauncher.exe"
& dotnet publish $launcherProject --nologo --configuration Release --output $launcherPublishDirectory --verbosity quiet | Out-Null
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $launcherPath -PathType Leaf)) {
    throw "Failed to publish hidden launcher: $launcherProject"
}

$arguments = @(
    (Quote-TaskArgument $powerShellPath),
    "-NoProfile",
    "-ExecutionPolicy", "Bypass",
    "-File", (Quote-TaskArgument $resumeScriptPath),
    "-TaskName", (Quote-TaskArgument $TaskName)
) -join " "

$currentUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
$action = New-ScheduledTaskAction -Execute $launcherPath -Argument $arguments
$logonTrigger = New-ScheduledTaskTrigger -AtLogOn -User $currentUser
$repetitionTrigger = New-ScheduledTaskTrigger `
    -Once `
    -At (Get-Date).Date `
    -RepetitionInterval (New-TimeSpan -Minutes 10) `
    -RepetitionDuration (New-TimeSpan -Days 3650)
$principal = New-ScheduledTaskPrincipal -UserId $currentUser -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 5)

Register-ScheduledTask `
    -TaskName $TaskName `
    -Action $action `
    -Trigger @($logonTrigger, $repetitionTrigger) `
    -Principal $principal `
    -Settings $settings `
    -Description "Resume the last journaled mcg orchestrator conduct loop after logon or loop crashes." `
    -Force | Out-Null

Write-Output "installed task=$TaskName triggers=logon,repeat-10-minutes script=$resumeScriptPath"
