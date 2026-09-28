@echo off
cd /d "%~dp0"
echo.
echo  [1/3] Stopping running instances...
taskkill /IM KontaktLibManager.exe /F >nul 2>&1
taskkill /IM KontaktLibManager.SelfTest.exe /F >nul 2>&1
ping -n 3 127.0.0.1 >nul
echo  [2/3] Building Release...
cd src\KontaktLibManager
dotnet build -c Release -v m --nologo
if errorlevel 1 (
    echo.
    echo  BUILD FAILED - please send me the errors above
    pause
    exit /b 1
)
cd ..\..
echo.
echo  [3/3] Starting...
start "" "src\KontaktLibManager\bin\Release\net9.0-windows\KontaktLibManager.exe"
echo.
echo  Started.
