@echo off
chcp 65001 >nul
setlocal
REM ==========================================================
REM  AspireCTF - 一键部署（薄封装）
REM  本地无仓库时 git clone 到 .\aspirectf，然后调用 start.bat 完成部署
REM  一键运行命令见 README.md（Windows 使用 PowerShell Invoke-WebRequest）
REM ==========================================================
cd /d "%~dp0"

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

docker info >nul 2>&1
if errorlevel 1 (
    echo [错误] Docker 服务未运行，请先启动 Docker Desktop
    exit /b 1
)

docker compose version >nul 2>&1
if errorlevel 1 (
    echo [错误] 未找到 docker compose，请安装 Docker Compose 插件
    exit /b 1
)

where git >nul 2>&1
if errorlevel 1 (
    echo [错误] 未找到 git，请安装 Git for Windows
    exit /b 1
)

REM ========== 定位仓库：本地已有则直接用，否则 clone 到 .\aspirectf ==========
set "REPO_DIR=%CD%"
if exist "start.bat" if exist "deploy\docker-compose.yaml" (
    findstr /c:"aspire-dashboard" "deploy\docker-compose.yaml" >nul 2>&1
    if not errorlevel 1 goto :have_repo
)

set "REPO_DIR=%CD%\aspirectf"
if exist "%REPO_DIR%" if not exist "%REPO_DIR%\.git" (
    echo [错误] %REPO_DIR% 已存在且不是 git 仓库，请移走该目录或换一个目录运行
    exit /b 1
)
if exist "%REPO_DIR%\.git" (
    echo [信息] %REPO_DIR% 已存在，git pull 更新...
    git -C "%REPO_DIR%" pull --ff-only https://gitee.com/li_pilit/aspirectf.git main >nul 2>&1
    if errorlevel 1 echo [提示] 更新失败，继续使用本地现有版本
) else (
    echo [信息] 克隆仓库到 %REPO_DIR% ...
    git clone https://gitee.com/li_pilit/aspirectf.git "%REPO_DIR%"
    if errorlevel 1 (
        echo [错误] 克隆失败，请检查网络连接
        exit /b 1
    )
)

:have_repo
echo [信息] 项目目录: %REPO_DIR%
echo [信息] 调用 start.bat 启动...
echo.
call "%REPO_DIR%\start.bat" %*
exit /b %errorlevel%
