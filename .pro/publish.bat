@echo off
setlocal EnableExtensions
chcp 65001 >nul
set "SCRIPT_DIR=%~dp0"
if not exist "%SCRIPT_DIR%publish.ps1" (
    echo [FAILED] publish.ps1 was not found.
    pause
    exit /b 1
)
if not exist "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" (
    echo [FAILED] Windows PowerShell was not found.
    pause
    exit /b 1
)
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%publish.ps1"
set "EXIT_CODE=%ERRORLEVEL%"
if not "%EXIT_CODE%"=="0" (
    echo.
    echo [FAILED] Build and package failed. Code: %EXIT_CODE%
    pause
)
exit /b %EXIT_CODE%
