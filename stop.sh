#!/usr/bin/env bash
# AspireCTF 一键停止
#   ./stop.sh          停止并删除容器（保留数据库/工作区数据卷）
#   ./stop.sh --purge  同时删除数据卷（任务、工作区全部清空，危险）
set -euo pipefail
cd "$(dirname "$0")"

if [ "${1:-}" = "--purge" ]; then
  docker compose -f deploy/docker-compose.yaml down -v
  echo "已停止并清除数据卷。"
else
  docker compose -f deploy/docker-compose.yaml down
  echo "已停止（数据卷保留；重新 ./start.sh 后任务仍在）。"
fi
