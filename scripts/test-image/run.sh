#!/usr/bin/env bash
echo "[test-sandbox] task=${TASK_ID:-unknown}"
echo "[test-sandbox] target=${TARGET_URL:-n/a}"
echo "[test-sandbox] workspace:"; ls -la /workspace
echo "FLAG{container_docker_pipeline_ok}"
echo "FLAG{container_docker_pipeline_ok}" > /workspace/flag.txt
echo "DONE"
exit 0
