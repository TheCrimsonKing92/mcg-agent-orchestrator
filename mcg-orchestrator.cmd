@echo off
setlocal
set "ROOT=%~dp0"
set "MCG_ORCHESTRATOR_REPOSITORY_ROOT=%ROOT%"
set "LOCK_DIR=%ROOT%.build-lock"
set "APP_PROJECT=%ROOT%src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj"
set "APP_DLL=%ROOT%src\Mcg.AgentOrchestrator.App\bin\Debug\net10.0\Mcg.AgentOrchestrator.App.dll"

:: Up-to-date check -- if App.dll exists and is newer than all source files, skip build entirely.
if exist "%APP_DLL%" (
    powershell -NoProfile -Command "$dll=Get-Item '%APP_DLL%';$stale=Get-ChildItem '%ROOT%src' -Recurse -Include *.cs,*.csproj,*.props -ErrorAction SilentlyContinue | Where-Object {$_.LastWriteTime -gt $dll.LastWriteTime} | Select-Object -First 1;if($stale){exit 1}else{exit 0}"
    if not errorlevel 1 goto run_app
)

:: Reclaim a dead-owner or age-stale lock left by a prior crashed invocation.
:: If owner.pid exists and the owner process is alive, do NOT reclaim.
:: If owner.pid is missing or unreadable, fall back to a generous 300-s age threshold.
if exist "%LOCK_DIR%" powershell -NoProfile -Command "$ld='%LOCK_DIR%';$pf=Join-Path $ld 'owner.pid';$re=$false;if(Test-Path $pf){try{$op=[int]((Get-Content $pf -Raw -ErrorAction Stop).Trim());if(-not(Get-Process -Id $op -ErrorAction SilentlyContinue)){$re=$true}}catch{$re=$true}}else{if(((Get-Date)-(Get-Item $ld).LastWriteTime).TotalSeconds -gt 300){$re=$true}};if($re){Remove-Item $ld -Recurse -Force -ErrorAction SilentlyContinue}"

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
:: Record our PID so concurrent invocations can test our liveness before reclaiming.
for /f "delims=" %%a in ('powershell -NoProfile -Command "(Get-Process -Id $PID).Parent.Id"') do set "MYPID=%%a"
if defined MYPID echo %MYPID%>"%LOCK_DIR%\owner.pid"

set "BUILD_LOG=%TEMP%\mcg-build-%RANDOM%.log"
dotnet build "%APP_PROJECT%" --nologo -v q >"%BUILD_LOG%" 2>&1
set BUILD_EXIT=%ERRORLEVEL%
rmdir /s /q "%LOCK_DIR%" 2>nul

if %BUILD_EXIT% neq 0 goto build_failed
del "%BUILD_LOG%" 2>nul

:run_app
dotnet "%APP_DLL%" %*
exit /b %ERRORLEVEL%

:build_failed
if not exist "%APP_DLL%" goto show_build_error
powershell -NoProfile -Command "try{$f=[IO.File]::Open('%APP_DLL%',[IO.FileMode]::Open,[IO.FileAccess]::Write);$f.Close();exit 1}catch{exit 0}"
if not errorlevel 1 (
    echo ERROR: build failed: App.dll is locked by a running orchestrator instance ^(serve-dashboard?^); stop it and retry >&2
    del "%BUILD_LOG%" 2>nul
    exit /b %BUILD_EXIT%
)
:show_build_error
type "%BUILD_LOG%" >&2
echo. >&2
echo ERROR: dotnet build failed >&2
del "%BUILD_LOG%" 2>nul
exit /b %BUILD_EXIT%
