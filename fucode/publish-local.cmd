@echo off
rem 一键本地构建：双击即可。构建产物写入 out/app，应用内"部署最新构建"按钮会扫描它。
setlocal DisableDelayedExpansion
set "SCRIPT_DIR=%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%publish-local.ps1" %*
if errorlevel 1 (
    echo.
    echo Build failed.
    pause
    exit /b 1
)
echo.
echo Done. The app was stopped, updated and restarted.
pause
