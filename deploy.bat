@echo off
setlocal
REM ==========================================================
REM  AspireCTF - 一键部署脚本 (Windows)
REM  检查环境 -> 生成 deploy\.env -> 拉取基础镜像 -> 构建应用镜像 -> 启动
REM  用法: 双击运行，或在 cmd 中执行 deploy.bat
REM ==========================================================
cd /d "%~dp0"

set "COMPOSE_FILE=deploy\docker-compose.yaml"
set "ENV_FILE=deploy\.env"

echo ==========================================
echo   AspireCTF - 一键部署
echo ==========================================
echo.

REM ========== 检查环境 ==========
where docker >nul 2>&1
if errorlevel 1 (
    echo [错误] Docker 未安装，请先安装 Docker Desktop
    echo 安装教程: https://docs.docker.com/get-docker/
    exit /b 1
)

docker compose version >nul 2>&1
if not errorlevel 1 (
    set "DC=docker compose"
) else (
    where docker-compose >nul 2>&1
    if errorlevel 1 (
        echo [错误] Docker Compose 未安装
        exit /b 1
    )
    set "DC=docker-compose"
)

docker info >nul 2>&1
if errorlevel 1 (
    echo [错误] Docker 服务未运行，请先启动 Docker Desktop
    exit /b 1
)

echo [信息] Docker:  & docker --version
echo [信息] Compose: %DC%
echo [信息] 项目目录: %CD%
echo.

REM ========== 首次部署生成 .env ==========
if not exist "%ENV_FILE%" (
    copy /y "deploy\.env.example" "%ENV_FILE%" >nul
    echo [提示] 首次部署，从模板生成配置 deploy\.env
    echo [完成] 已生成 deploy\.env
    echo [提示] 如需修改端口或 AI 密钥，请编辑 deploy\.env 后重新运行本脚本
    echo.
)

REM ========== 部署 ==========
echo [1/4] 拉取基础镜像（postgres / aspire-dashboard，旧容器保持运行）...
%DC% -f "%COMPOSE_FILE%" pull postgres aspire-dashboard
if errorlevel 1 (
    echo [错误] 镜像拉取失败，请检查网络连接
    exit /b 1
)
echo [完成] 基础镜像拉取完成
echo.

echo [2/4] 构建应用镜像（apiservice / web / demo-target）...
%DC% -f "%COMPOSE_FILE%" build apiservice web demo-target
if errorlevel 1 (
    echo [错误] 镜像构建失败，请检查上方错误输出
    exit /b 1
)
echo [完成] 应用镜像构建完成
echo.

echo [3/4] 停止旧容器（仅本项目）...
%DC% -f "%COMPOSE_FILE%" down --remove-orphans >nul 2>&1
echo [完成] 旧容器已清理
echo.

echo [4/4] 启动服务...
%DC% -f "%COMPOSE_FILE%" up -d
if errorlevel 1 (
    echo [错误] 服务启动失败，查看日志: %DC% -f "%COMPOSE_FILE%" logs
    exit /b 1
)
echo [完成] 服务已启动
echo.

echo [信息] 等待服务就绪...
timeout /t 10 /nobreak >nul
%DC% -f "%COMPOSE_FILE%" ps
echo.

REM ========== 读取端口配置 ==========
set "WEB_PORT=8080"
set "DEMO_PORT=8088"
set "DASHBOARD_PORT=18888"
set "POSTGRES_PORT=5432"
if exist "%ENV_FILE%" (
    for /f "usebackq tokens=1,* delims==" %%A in ("%ENV_FILE%") do (
        if "%%A"=="WEB_PORT" set "WEB_PORT=%%B"
        if "%%A"=="DEMO_PORT" set "DEMO_PORT=%%B"
        if "%%A"=="DASHBOARD_PORT" set "DASHBOARD_PORT=%%B"
        if "%%A"=="POSTGRES_PORT" set "POSTGRES_PORT=%%B"
    )
)

echo ==========================================
echo   部署完成！
echo ==========================================
echo.
echo 服务访问地址:
echo   Web:        http://localhost:%WEB_PORT%
echo   Demo 靶场:   http://localhost:%DEMO_PORT%/login
echo   Dashboard:  http://localhost:%DASHBOARD_PORT%
echo   Postgres:   localhost:%POSTGRES_PORT% (db=ctfdb / user=postgres)
echo.
echo 常用命令:
echo   查看日志: %DC% -f "%COMPOSE_FILE%" logs -f web apiservice
echo   停止服务: %DC% -f "%COMPOSE_FILE%" down
echo   重启服务: %DC% -f "%COMPOSE_FILE%" restart
echo   更新版本: 重新运行 deploy.bat
echo.
echo 提示: 首次解题时沙箱会自动构建 ctf-opencode-sandbox 镜像 (约 1-2 分钟)
echo.
exit /b 0
