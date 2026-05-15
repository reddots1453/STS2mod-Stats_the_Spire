@echo off
setlocal
cd /d "%~dp0"

echo ============================================
echo  Stats the Spire - Mod Update Script
echo ============================================
echo.

set EDITION=local
if /i "%~1"=="community" set EDITION=community
echo Edition: %EDITION%
echo.

powershell -NoProfile -ExecutionPolicy Bypass -Command "$info = Invoke-RestMethod -Uri 'https://statsthespire.org.cn/v1/meta/update-info?edition=%EDITION%&current=0.0.0' -TimeoutSec 15; if (-not $info.update_available) { Write-Host 'Already up to date.'; exit 0 }; Write-Host ('Latest version: ' + $info.latest); Write-Host 'Downloading...'; $url = 'https://statsthespire.org.cn' + $info.download_url; Invoke-WebRequest -Uri $url -OutFile 'sts2_statsthespire_local.dll.new' -TimeoutSec 60; if (-not (Test-Path 'sts2_statsthespire_local.dll.new')) { Write-Host 'ERROR: Download failed.'; pause; exit 1 }; $size = (Get-Item 'sts2_statsthespire_local.dll.new').Length; Write-Host ('Downloaded ' + $size + ' bytes.')"

if %errorlevel% neq 0 (
    pause
    exit /b 1
)

echo.
echo Replacing sts2_statsthespire_local.dll ...
move /Y "sts2_statsthespire_local.dll.new" "sts2_statsthespire_local.dll"
if %errorlevel% equ 0 (
    echo.
    echo ============================================
    echo  Update applied successfully!
    echo  You can now start the game.
    echo ============================================
) else (
    echo.
    echo ERROR: Could not replace DLL. Is the game still running?
    echo Close the game and run this script again.
)
pause
