@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

if /i "%~1"=="start" goto :start
if /i "%~1"=="update" goto :update
if /i "%~1"=="stop" goto :stop
if "%~1"=="" goto :menu
if /i "%~1"=="help" goto :usage
if /i "%~1"=="-h" goto :usage

:usage
echo Usage: aspirectf.bat [start^|update^|stop] [args]
echo.
echo   aspirectf.bat start
echo   aspirectf.bat update
echo   aspirectf.bat stop
echo   aspirectf.bat stop --purge
exit /b 2

:menu
echo ==========================================
echo   AspireCTF Control
echo ==========================================
echo.
echo   1 - Start or rebuild services
echo   2 - Update code and rebuild services
echo   3 - Stop services, keep data
echo   0 - Exit
echo.
set "MENU_CHOICE="
set /p "MENU_CHOICE=Select [0-3]: "
if "%MENU_CHOICE%"=="0" exit /b 0
if "%MENU_CHOICE%"=="1" goto :start
if "%MENU_CHOICE%"=="2" goto :update
if "%MENU_CHOICE%"=="3" goto :stop
echo [INFO] Invalid option. Enter 0, 1, 2 or 3.
goto :menu

:start
call "%~dp0start.bat" %~2 %~3 %~4 %~5 %~6 %~7 %~8 %~9
exit /b %errorlevel%

:update
call "%~dp0update.bat" %~2 %~3 %~4 %~5 %~6 %~7 %~8 %~9
exit /b %errorlevel%

:stop
call "%~dp0stop.bat" %~2 %~3 %~4 %~5 %~6 %~7 %~8 %~9
exit /b %errorlevel%
