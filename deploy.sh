#!/bin/bash
# ==========================================================
#  AspireCTF - 一键部署脚本
#  检查环境 → 生成 deploy/.env → 拉取基础镜像 → 构建应用镜像 → 启动
#  用法: bash deploy.sh
# ==========================================================

set -e

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m'

WORK_DIR="$(cd "$(dirname "$0")" && pwd)"
COMPOSE_FILE="$WORK_DIR/deploy/docker-compose.yaml"
ENV_FILE="$WORK_DIR/deploy/.env"
ENV_EXAMPLE="$WORK_DIR/deploy/.env.example"

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

if docker compose version &> /dev/null; then
    DC="docker compose"
elif command -v docker-compose &> /dev/null; then
    DC="docker-compose"
else
    echo -e "${RED}错误: Docker Compose 未安装${NC}"
    exit 1
fi

if ! docker info &> /dev/null; then
    echo -e "${RED}错误: Docker 服务未运行，请先启动 Docker${NC}"
    exit 1
fi

DC_CMD="$DC -f $COMPOSE_FILE"

echo -e "${CYAN}[信息] Docker:  $(docker --version)${NC}"
echo -e "${CYAN}[信息] Compose: $DC${NC}"
echo -e "${CYAN}[信息] 项目目录: $WORK_DIR${NC}"
echo ""

# ========== 首次部署生成 .env ==========
if [ ! -f "$ENV_FILE" ]; then
    echo -e "${YELLOW}[提示] 首次部署，从模板生成配置 deploy/.env${NC}"
    cp "$ENV_EXAMPLE" "$ENV_FILE"
    echo -e "${GREEN}✓ 已生成 deploy/.env${NC}"
    echo -e "${YELLOW}[提示] 如需修改端口或 AI 密钥，请编辑 deploy/.env 后重新运行本脚本${NC}"
    echo ""
fi

# ========== 部署 ==========
echo -e "${YELLOW}步骤 1/4: 拉取基础镜像（postgres / aspire-dashboard，旧容器保持运行）...${NC}"
$DC_CMD pull postgres aspire-dashboard
echo -e "${GREEN}✓ 基础镜像拉取完成${NC}"
echo ""

echo -e "${YELLOW}步骤 2/4: 构建应用镜像（apiservice / web / demo-target）...${NC}"
$DC_CMD build apiservice web demo-target
echo -e "${GREEN}✓ 应用镜像构建完成${NC}"
echo ""

echo -e "${YELLOW}步骤 3/4: 停止旧容器（仅本项目）...${NC}"
$DC_CMD down --remove-orphans 2>/dev/null || true
echo -e "${GREEN}✓ 旧容器已清理${NC}"
echo ""

echo -e "${YELLOW}步骤 4/4: 启动服务...${NC}"
$DC_CMD up -d
echo -e "${GREEN}✓ 服务已启动${NC}"
echo ""

echo "[信息] 等待服务就绪..."
sleep 10
$DC_CMD ps
echo ""

# ========== 读取端口配置 ==========
read_env() {
    local key="$1" default="$2" val=""
    val=$(grep -E "^${key}=" "$ENV_FILE" 2>/dev/null | head -n1 | cut -d '=' -f 2- | tr -d '\r' || true)
    echo "${val:-$default}"
}

web_port=$(read_env WEB_PORT 8080)
demo_port=$(read_env DEMO_PORT 8088)
dashboard_port=$(read_env DASHBOARD_PORT 18888)
postgres_port=$(read_env POSTGRES_PORT 5432)

echo ""
echo -e "${GREEN}==========================================${NC}"
echo -e "${GREEN}  部署完成！${NC}"
echo -e "${GREEN}==========================================${NC}"
echo ""
echo "服务访问地址："
echo "  Web:       http://服务器IP:${web_port}"
echo "  Demo 靶场:  http://服务器IP:${demo_port}/login"
echo "  Dashboard: http://服务器IP:${dashboard_port}"
echo "  Postgres:  服务器IP:${postgres_port}（db=ctfdb / user=postgres）"
echo ""
echo "常用命令："
echo "  查看日志: $DC_CMD logs -f web apiservice"
echo "  停止服务: $DC_CMD down"
echo "  重启服务: $DC_CMD restart"
echo "  更新版本: 重新运行 bash deploy.sh"
echo ""
echo "提示: 首次解题时沙箱会自动构建 ctf-opencode-sandbox 镜像（约 1-2 分钟）。"
