#!/bin/bash
# ==========================================================
#  AspireCTF - 更新脚本
#  git pull 最新代码，然后调用 start.sh 重建并启动
#  一键运行: curl -fsSL https://gitee.com/li_pilit/aspirectf/raw/main/update.sh | sed 's/\r$//' | bash
# ==========================================================
set -e

RED='\033[0;31m'
GREEN='\033[0;32m'
CYAN='\033[0;36m'
NC='\033[0m'

REPO_URL="https://gitee.com/li_pilit/aspirectf.git"
WORK_DIR="$(cd "$(dirname "$0")" && pwd)"

echo "=========================================="
echo "  AspireCTF - 更新部署"
echo "=========================================="
echo ""

if ! command -v git &> /dev/null; then
    echo -e "${RED}错误: 未找到 git，请先安装 git${NC}"
    exit 1
fi

if ! command -v docker &> /dev/null || ! docker info &> /dev/null; then
    echo -e "${RED}错误: Docker 未安装或服务未运行${NC}"
    exit 1
fi

if ! docker compose version &> /dev/null; then
    echo -e "${RED}错误: 未找到 docker compose，请安装 Docker Compose 插件${NC}"
    exit 1
fi

if [ -f "$WORK_DIR/start.sh" ] && [ -f "$WORK_DIR/deploy/docker-compose.yaml" ] && grep -q 'aspire-dashboard' "$WORK_DIR/deploy/docker-compose.yaml"; then
    REPO_DIR="$WORK_DIR"
else
    REPO_DIR="$WORK_DIR/aspirectf"
    if [ -d "$REPO_DIR" ] && [ ! -d "$REPO_DIR/.git" ]; then
        echo -e "${RED}错误: $REPO_DIR 已存在且不是 git 仓库${NC}"
        exit 1
    fi
fi

if [ -d "$REPO_DIR/.git" ]; then
    echo -e "${CYAN}[信息] 拉取 Gitee 最新代码...${NC}"
    git -C "$REPO_DIR" pull --ff-only "$REPO_URL" main
else
    echo -e "${CYAN}[信息] 未找到仓库，克隆到 $REPO_DIR ...${NC}"
    git clone "$REPO_URL" "$REPO_DIR"
fi

echo -e "${GREEN}[信息] 调用 start.sh 重新构建并启动...${NC}"
bash "$REPO_DIR/start.sh" "$@"
