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
echo.
echo   1 - 启动 / 重建服务
echo   2 - 更新代码并重建服务
echo   3 - 停止服务，保留数据卷
echo   0 - 退出
echo.
set "MENU_CHOICE="
set /p "MENU_CHOICE=请选择 [0-3]: "
if "%MENU_CHOICE%"=="0" exit /b 0
if "%MENU_CHOICE%"=="1" goto :start
if "%MENU_CHOICE%"=="2" goto :update
if "%MENU_CHOICE%"=="3" goto :stop
echo [提示] 无效选项，请输入 0、1、2 或 3。
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
