@echo off
chcp 65001 >nul
setlocal
REM ==========================================================
REM  AspireCTF - 更新脚本
REM  git pull 最新代码，然后调用 start.bat 重建并启动
REM  一键运行命令见 README.md（Windows 使用 PowerShell Invoke-WebRequest）
REM ==========================================================
cd /d "%~dp0"

echo ==========================================
echo   AspireCTF - 更新部署
echo ==========================================
echo.

where git >nul 2>&1
if errorlevel 1 (
    echo [错误] 未找到 git，请安装 Git for Windows
    exit /b 1
)

where docker >nul 2>&1
if errorlevel 1 (
    echo [错误] Docker 未安装，请先安装 Docker Desktop
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

set "REPO_DIR=%CD%"
if exist "start.bat" if exist "deploy\docker-compose.yaml" (
    findstr /c:"aspire-dashboard" "deploy\docker-compose.yaml" >nul 2>&1
    if not errorlevel 1 goto :have_repo
)

set "REPO_DIR=%CD%\aspirectf"
if exist "%REPO_DIR%" if not exist "%REPO_DIR%\.git" (
    echo [错误] %REPO_DIR% 已存在且不是 git 仓库
    exit /b 1
)

:have_repo
if exist "%REPO_DIR%\.git" (
    echo [信息] 拉取 Gitee 最新代码...
    git -C "%REPO_DIR%" pull --ff-only https://gitee.com/li_pilit/aspirectf.git main
    if errorlevel 1 (
        echo [错误] git pull 失败，请处理本地修改或冲突后重试
        exit /b 1
    )
) else (
    echo [信息] 未找到仓库，克隆到 %REPO_DIR% ...
    git clone https://gitee.com/li_pilit/aspirectf.git "%REPO_DIR%"
    if errorlevel 1 (
        echo [错误] 克隆失败，请检查网络连接
        exit /b 1
    )
)

echo [信息] 调用 start.bat 重新构建并启动...
call "%REPO_DIR%\start.bat" %*
exit /b %errorlevel%
