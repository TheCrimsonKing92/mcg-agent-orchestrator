@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0FakeRunner.ps1" %*
exit /b %ERRORLEVEL%