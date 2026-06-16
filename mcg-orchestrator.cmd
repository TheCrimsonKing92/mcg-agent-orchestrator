@echo off
setlocal
set "ROOT=%~dp0"
set "MCG_ORCHESTRATOR_REPOSITORY_ROOT=%ROOT%"
set "LOCK_DIR=%ROOT%.build-lock"
set "APP_PROJECT=%ROOT%src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj"
set "APP_DLL=%ROOT%src\Mcg.AgentOrchestrator.App\bin\Debug\net10.0\Mcg.AgentOrchestrator.App.dll"

:: Reclaim stale lock (left by a prior crashed invocation).
:: PowerShell exits 1 if the lock is older than 60 s (stale), 0 if fresh.
if exist "%LOCK_DIR%" powershell -NoProfile -Command "exit [int](((Get-Date)-(Get-Item '%LOCK_DIR%').LastWriteTime).TotalSeconds -gt 60)"
if exist "%LOCK_DIR%" if errorlevel 1 rmdir "%LOCK_DIR%" 2>nul

:: Acquire build lock -- mkdir is atomic on NTFS; spin/retry up to 30 s
set "LOCK_TRIES=0"
:try_lock
mkdir "%LOCK_DIR%" 2>nul
if not errorlevel 1 goto locked
set /a LOCK_TRIES+=1
if %LOCK_TRIES% geq 30 (
    echo ERROR: could not acquire build lock after 30 s >&2
    exit /b 1
)
ping -n 2 -w 500 127.0.0.1 >nul 2>&1
goto try_lock

:locked
dotnet build "%APP_PROJECT%" --nologo -v q >nul 2>&1
set BUILD_EXIT=%ERRORLEVEL%
rmdir "%LOCK_DIR%" 2>nul
if %BUILD_EXIT% neq 0 exit /b %BUILD_EXIT%

dotnet "%APP_DLL%" %*
exit /b %ERRORLEVEL%
