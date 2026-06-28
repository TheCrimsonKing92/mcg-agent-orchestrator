@echo off
setlocal
set "ROOT=%~dp0"
set "MCG_ORCHESTRATOR_REPOSITORY_ROOT=%ROOT%"
set "LOCK_DIR=%ROOT%.build-lock"
set "LOCK_STALE_SECONDS=60"
set "APP_PROJECT=%ROOT%src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj"
set "APP_DLL=%ROOT%src\Mcg.AgentOrchestrator.App\bin\Debug\net10.0\Mcg.AgentOrchestrator.App.dll"
set "APP_HEAD=%ROOT%src\Mcg.AgentOrchestrator.App\bin\Debug\net10.0\Mcg.AgentOrchestrator.App.dll.git-head"

:: Reclaim a dead-owner or age-stale lock left by a prior crashed invocation.
:: A lock older than LOCK_STALE_SECONDS is stale even if owner.pid is present.
:: A younger lock is also reclaimed when owner.pid is missing or its owner is gone.
if exist "%LOCK_DIR%" powershell -NoProfile -Command "$ld='%LOCK_DIR%';$threshold=%LOCK_STALE_SECONDS%;$re=$false;if(Test-Path -LiteralPath $ld){try{$age=((Get-Date)-(Get-Item -LiteralPath $ld -ErrorAction Stop).LastWriteTime).TotalSeconds;if($age -gt $threshold){$re=$true}}catch{$re=$true};if(-not $re){$pf=Join-Path $ld 'owner.pid';if(Test-Path -LiteralPath $pf){try{$op=[int]((Get-Content -LiteralPath $pf -Raw -ErrorAction Stop).Trim());if(-not (Get-Process -Id $op -ErrorAction SilentlyContinue)){$re=$true}}catch{$re=$true}}else{$re=$true}}};if($re){Remove-Item -LiteralPath $ld -Recurse -Force -ErrorAction SilentlyContinue}"

:: Up-to-date check -- if App.dll exists, matches the current git HEAD, and is newer than all
:: source files, skip build entirely. The HEAD marker catches merges where source timestamps do
:: not reliably make the existing binary look stale.
if exist "%APP_DLL%" (
    powershell -NoProfile -Command "$dll=Get-Item '%APP_DLL%';$gitHead='';try{$gitHead=(& git -C '%ROOT%' rev-parse HEAD 2>$null).Trim()}catch{};$marker='%APP_HEAD%';$headOk=($gitHead -eq '' -or ((Test-Path -LiteralPath $marker) -and ((Get-Content -Raw -LiteralPath $marker).Trim() -eq $gitHead)));$stale=Get-ChildItem '%ROOT%src' -Recurse -Include *.cs,*.csproj,*.props -ErrorAction SilentlyContinue | Where-Object {$_.LastWriteTime -gt $dll.LastWriteTime} | Select-Object -First 1;if($headOk -and -not $stale){exit 0}else{exit 1}"
    if not errorlevel 1 goto run_app
)

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
dotnet build "%APP_PROJECT%" --nologo -v quiet -clp:ErrorsOnly >"%BUILD_LOG%" 2>&1
set BUILD_EXIT=%ERRORLEVEL%
rmdir /s /q "%LOCK_DIR%" 2>nul

if %BUILD_EXIT% neq 0 goto build_failed
powershell -NoProfile -Command "try{$gitHead=(& git -C '%ROOT%' rev-parse HEAD 2>$null).Trim();if($gitHead){Set-Content -LiteralPath '%APP_HEAD%' -Value $gitHead -NoNewline -Encoding ASCII}}catch{}"
del "%BUILD_LOG%" 2>nul

:run_app
:: Run from a per-build ISOLATED copy of the binary, not the in-tree output. A live run then holds its own
:: copy (under %TEMP%\mcg-run\<build-hash>), leaving the in-tree binary free to rebuild while it runs -- so
:: builds and real runs stop interfering. Content-addressed by the App.dll hash: identical builds reuse one
:: copy, a new build gets a fresh one, and copies unused for 7 days are pruned (a live copy's dll is locked,
:: so it survives the prune). Falls back to the in-tree binary if the isolation step fails.
set "RUNDIR="
for /f "usebackq delims=" %%R in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%scripts\resolve-run-dir.ps1" "%APP_DLL%"`) do set "RUNDIR=%%R"
if not defined RUNDIR set "RUNDIR=%ROOT%src\Mcg.AgentOrchestrator.App\bin\Debug\net10.0"
dotnet "%RUNDIR%\Mcg.AgentOrchestrator.App.dll" %*
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
