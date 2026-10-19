#!/bin/bash
# ==========================================================
#  AspireCTF - 一键部署（薄封装）
#  本地无仓库时 git clone 到 ./aspirectf，然后调用 start.sh 完成部署
#  一键运行: curl -fsSL https://gitee.com/li_pilit/aspirectf/raw/main/deploy.sh | sed 's/\r$//' | bash
# ==========================================================
set -e

RED='\033[0;31m'
GREEN='\033[0;32m'
CYAN='\033[0;36m'
NC='\033[0m'

REPO_URL="https://gitee.com/li_pilit/aspirectf.git"
WORK_DIR="$(cd "$(dirname "$0")" && pwd)"

echo "=========================================="
echo "  AspireCTF - 一键部署"
echo "=========================================="
echo ""

# ========== 检查环境 ==========
if ! command -v docker &> /dev/null; then
    echo -e "${RED}错误: Docker 未安装，请先安装 Docker${NC}"
    echo "安装教程: https://docs.docker.com/get-docker/"
    exit 1
fi

if ! docker info &> /dev/null; then
    echo -e "${RED}错误: Docker 服务未运行，请先启动 Docker${NC}"
    exit 1
fi

if ! docker compose version &> /dev/null; then
    echo -e "${RED}错误: 未找到 docker compose，请安装 Docker Compose 插件${NC}"
    exit 1
fi

if ! command -v git &> /dev/null; then
    echo -e "${RED}错误: 未找到 git，请先安装 git${NC}"
    exit 1
fi

# ========== 定位仓库：本地已有则直接用，否则 clone 到 ./aspirectf ==========
if [ -f "$WORK_DIR/start.sh" ] && [ -f "$WORK_DIR/deploy/docker-compose.yaml" ] && grep -q 'aspire-dashboard' "$WORK_DIR/deploy/docker-compose.yaml"; then
    REPO_DIR="$WORK_DIR"
else
    REPO_DIR="$WORK_DIR/aspirectf"
    if [ -d "$REPO_DIR" ] && [ ! -d "$REPO_DIR/.git" ]; then
        echo -e "${RED}错误: $REPO_DIR 已存在且不是 git 仓库，请移走该目录或换一个目录运行${NC}"
        exit 1
    fi
    if [ -d "$REPO_DIR/.git" ]; then
        echo -e "${CYAN}[信息] $REPO_DIR 已存在，git pull 更新...${NC}"
        git -C "$REPO_DIR" pull --ff-only "$REPO_URL" main 2>/dev/null || echo -e "${CYAN}[提示] 更新失败，继续使用本地现有版本${NC}"
    else
        echo -e "${CYAN}[信息] 克隆仓库到 $REPO_DIR ...${NC}"
        git clone "$REPO_URL" "$REPO_DIR"
        echo -e "${GREEN}✓ 克隆完成${NC}"
    fi
fi

echo -e "${CYAN}[信息] 项目目录: $REPO_DIR${NC}"
echo -e "${CYAN}[信息] 调用 start.sh 启动...${NC}"
echo ""

# ========== 调用 start：生成 .env / 构建镜像 / 启动 / 输出访问地址 ==========
bash "$REPO_DIR/start.sh" "$@"
