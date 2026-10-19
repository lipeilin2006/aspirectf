#!/usr/bin/env bash
set -euo pipefail

log() { echo "[api-entrypoint] $*"; }

mkdir -p /data/workspaces /run/podman /var/lib/containers/storage

# 嵌套 cgroup v2 无法启用 pids 控制器（crun: "pids is not available"）。
# 旧镜像可能内置 pids_limit = -1（仍会触碰控制器导致启动失败），
# 这里统一修正为 0（podman 会完全省略 pids 控制器），保证容器重建后自愈。
CONF=/etc/containers/containers.conf
if [ -f "$CONF" ]; then
    if grep -qE '^\s*pids_limit\s*=' "$CONF"; then
        sed -i 's/^\s*pids_limit\s*=.*/pids_limit = 0/' "$CONF"
    else
        printf '\n[containers]\npids_limit = 0\n' >> "$CONF"
    fi
    log "containers.conf pids_limit forced to 0 (nested cgroup pids workaround)"
fi

# Enable forwarding for nested Podman networking. The sysctl may be restricted
# by the outer runtime, so keep startup alive and report the actual result.
if sysctl -w net.ipv4.ip_forward=1 >/dev/null 2>&1; then
    log "IPv4 forwarding enabled"
else
    log "WARN: unable to enable net.ipv4.ip_forward"
fi
if sysctl -w net.ipv6.conf.all.forwarding=1 >/dev/null 2>&1; then
    log "IPv6 forwarding enabled"
else
    log "WARN: unable to enable net.ipv6.conf.all.forwarding"
fi

log "starting podman system service (podman-in-podman) ..."
podman system service --time=0 unix:///run/podman/podman.sock &
PODMAN_SERVICE_PID=$!

for i in $(seq 1 60); do
    if [ -S /run/podman/podman.sock ]; then
        log "podman socket ready"
        break
    fi
    sleep 0.5
done

# 预拉取/构建沙箱镜像（ctf-opencode-sandbox）。
# 不能在 API 启动前同步构建：首次构建可能需要数分钟，Web 会在此期间
# 访问 8080 并得到 connection refused。API 先监听，沙箱在后台准备。
SANDBOX_IMAGE="${SANDBOX_IMAGE:-ctf-opencode-sandbox:latest}"
# 基础镜像源列表（空格分隔，每项是 /debian 之前的前缀）。
# 单一镜像源经常返回 403（Cloudflare 1034），按顺序轮换直到成功。
SANDBOX_BASE_REGISTRIES="${SANDBOX_BASE_REGISTRIES:-${SANDBOX_BASE_REGISTRY:-docker.1ms.run/library} docker.m.daocloud.io/library docker.1panel.live/library hub.rat.dev/library public.ecr.aws/docker/library}"
SANDBOX_BUILD_ROUNDS="${SANDBOX_BUILD_ROUNDS:-3}"
SANDBOX_STATUS_FILE="/var/log/ctf-sandbox-build.status"
SANDBOX_BUILD_LOG="/var/log/ctf-sandbox-build.log"
if ! podman image exists "$SANDBOX_IMAGE" 2>/dev/null; then
    printf 'building\n' > "$SANDBOX_STATUS_FILE"
    if [ -f /opt/sandbox/Dockerfile ]; then
        log "building sandbox image $SANDBOX_IMAGE in background (first start may take a while)..."
        (
            echo "[sandbox-build] started: $(date -Iseconds)"
            success=0
            round=1
            while [ "$round" -le "$SANDBOX_BUILD_ROUNDS" ] && [ "$success" -eq 0 ]; do
                for registry in $SANDBOX_BASE_REGISTRIES; do
                    registry="${registry%/}"
                    case "$registry" in */*) ;; *) registry="$registry/library" ;; esac
                    echo "[sandbox-build] round $round: trying $registry/debian:trixie-slim"
                    if podman build --network host --build-arg "BASE_REGISTRY=$registry" -t "$SANDBOX_IMAGE" /opt/sandbox; then
                        echo "[sandbox-build] base image source accepted: $registry"
                        success=1
                        break
                    fi
                    echo "[sandbox-build] round $round rejected by $registry; next source in 5s"
                    sleep 5
                done
                if [ "$success" -eq 0 ] && [ "$round" -lt "$SANDBOX_BUILD_ROUNDS" ]; then
                    echo "[sandbox-build] round $round: all sources failed; full retry in 10s"
                    sleep 10
                fi
                round=$((round + 1))
            done
            if [ "$success" -eq 1 ]; then
                printf 'ready\n' > "$SANDBOX_STATUS_FILE"
                log "sandbox image $SANDBOX_IMAGE is ready"
            else
                printf 'failed\n' > "$SANDBOX_STATUS_FILE"
                log "WARN: sandbox image build failed after $SANDBOX_BUILD_ROUNDS rounds of base registry sources; tasks are blocked"
            fi
        ) > "$SANDBOX_BUILD_LOG" 2>&1 &
    else
        log "pulling sandbox image $SANDBOX_IMAGE in background ..."
        (
            echo "[sandbox-build] started: $(date -Iseconds)"
            if podman pull "$SANDBOX_IMAGE"; then
                printf 'ready\n' > "$SANDBOX_STATUS_FILE"
                log "sandbox image $SANDBOX_IMAGE is ready"
            else
                printf 'failed\n' > "$SANDBOX_STATUS_FILE"
                log "WARN: sandbox image pull failed; tasks are blocked"
            fi
        ) > "$SANDBOX_BUILD_LOG" 2>&1 &
    fi
else
    printf 'ready\n' > "$SANDBOX_STATUS_FILE"
    log "sandbox image $SANDBOX_IMAGE already present"
fi

log "starting AspireCTF.Api ..."
exec dotnet AspireCTF.Api.dll
