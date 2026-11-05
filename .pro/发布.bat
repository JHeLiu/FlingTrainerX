@echo off
rem ============================================================
rem  FengLingYueYingZong Storage Bag - production build launcher
rem  Double-click to run. Everything else is in publish.ps1.
rem ============================================================
chcp 65001 >nul
title FTX - Production Build
cd /d "%~dp0"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" %*
if errorlevel 1 (
    echo.
    echo  [build failed] see the message above.
    echo.
    pause
    exit /b 1
)

exit /b 0
