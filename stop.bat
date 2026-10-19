@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

rem AspireCTF one-click stop
rem   stop.bat            stop and remove containers (keep data volumes)
rem   stop.bat --purge    also remove data volumes (DANGER: wipes tasks/workspaces)
if /i "%~1"=="--purge" goto purge
if /i "%~1"=="purge" goto purge

docker compose -f "deploy\docker-compose.yaml" down
if errorlevel 1 exit /b 1
echo Stopped. Data volumes kept - run start.bat to bring it back with tasks intact.
endlocal
exit /b 0

:purge
docker compose -f "deploy\docker-compose.yaml" down -v
if errorlevel 1 exit /b 1
echo Stopped and data volumes removed.
endlocal
exit /b 0
