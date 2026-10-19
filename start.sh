#!/usr/bin/env bash
# AspireCTF 一键启动（docker compose，compose 文件在 deploy/ 下）
#   ./start.sh            构建镜像并启动（首次会构建 apiservice/web/demo-target）
#   ./start.sh --build    强制重新构建
set -euo pipefail
cd "$(dirname "$0")"

[ -f deploy/.env ] || cp deploy/.env.example deploy/.env

docker compose -f deploy/docker-compose.yaml up -d --build "$@"

cat <<EOF

AspireCTF 已启动:
  Web      http://localhost:8080
  Demo 靶场 http://localhost:8088/login
  Dashboard http://localhost:18888

提示: 首次解题时沙箱会在容器内构建 ctf-opencode-sandbox 镜像（约 1-2 分钟）。
查看日志: docker compose -f deploy/docker-compose.yaml logs -f web apiservice
EOF
