@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

rem AspireCTF one-click start (docker compose)
if not exist "deploy\.env" copy /y "deploy\.env.example" "deploy\.env" >nul

docker compose -f "deploy\docker-compose.yaml" up -d --build
if errorlevel 1 (
    echo.
    echo [ERROR] docker compose up failed.
    exit /b 1
)

echo.
echo AspireCTF started:
echo   Web       http://localhost:8080
echo   Demo      http://localhost:8088/login
echo   Dashboard http://localhost:18888
echo.
echo Note: the sandbox image ctf-opencode-sandbox is built inside the
echo       container on first solve (about 1-2 minutes).
echo Logs: docker compose -f "deploy\docker-compose.yaml" logs -f web apiservice
endlocal
