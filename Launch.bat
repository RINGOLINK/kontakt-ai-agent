@echo off
cd /d "%~dp0"
echo.
echo  Starting Kontakt Library Manager...
start "" "src\KontaktLibManager\bin\Release\net9.0-windows\KontaktLibManager.exe"
echo  Started. (data dir = exe\data, no env var needed)
