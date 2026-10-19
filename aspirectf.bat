@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

if /i "%~1"=="start" goto :start
if /i "%~1"=="update" goto :update
if /i "%~1"=="stop" goto :stop
if /i "%~1"=="" goto :menu
if /i "%~1"=="help" goto :usage
if /i "%~1"=="-h" goto :usage

:usage
echo 用法: aspirectf.bat [start^|update^|stop] [参数]
echo.
echo   aspirectf.bat start
echo   aspirectf.bat update
echo   aspirectf.bat stop
echo   aspirectf.bat stop --purge
exit /b 2

:menu
echo ==========================================
echo   AspireCTF 控制台
echo ==========================================
choice /c 1230 /n /m "请选择 [1-3/0]: "
if errorlevel 4 exit /b 0
if errorlevel 3 goto :stop
if errorlevel 2 goto :update
goto :start

:start
call "%~dp0start.bat" %~2 %~3 %~4 %~5 %~6 %~7 %~8 %~9
exit /b %errorlevel%

:update
call "%~dp0update.bat" %~2 %~3 %~4 %~5 %~6 %~7 %~8 %~9
exit /b %errorlevel%

:stop
call "%~dp0stop.bat" %~2 %~3 %~4 %~5 %~6 %~7 %~8 %~9
exit /b %errorlevel%
