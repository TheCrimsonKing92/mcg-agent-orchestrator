@echo off
setlocal EnableExtensions EnableDelayedExpansion
echo started>"C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\26066a2c\.mtp-sandbox-0e82ab03\build-started.txt"
echo CMDCMDLINE=%CMDCMDLINE%>"C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\26066a2c\.mtp-sandbox-0e82ab03\build-launch.txt"
:mcg_argument_loop
if "%~1"=="" goto mcg_arguments_done
set "mcg_argument=%~1"
echo ARG=!mcg_argument!>>"C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\26066a2c\.mtp-sandbox-0e82ab03\build-launch.txt"
shift
goto mcg_argument_loop
:mcg_arguments_done
echo compiler diagnostic from stub 1>&2
exit /b 0
