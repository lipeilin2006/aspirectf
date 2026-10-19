#!/usr/bin/env bash
# 沙箱常驻入口：容器创建后保持运行，解题由编排器通过 `podman exec` 触发
# /opt/sandbox/run-challenge.sh。容器持久化以保留 opencode 会话历史与工作区状态，
# 只有任务被删除时编排器才会移除容器。
set -u

echo "[sandbox] sandbox daemon ready at $(date -Iseconds)"

term=0
on_term() { term=1; }
trap on_term TERM INT

while [ "$term" -eq 0 ]; do
    sleep 3600 &
    wait $! || true
done

echo "[sandbox] sandbox daemon stopping at $(date -Iseconds)"
