# CTF 解题沙箱镜像：OpenCode CLI + Python 运行时（最小依赖，工具按需自装）
# 构建： docker build -t ctf-opencode-sandbox:latest sandbox/

# 允许 api-entrypoint 在镜像源返回 403/超时时切换到其它源。
# 入口脚本传入的前缀包含 library 命名空间（如 public.ecr.aws/docker/library）。
ARG BASE_REGISTRY=docker.1ms.run/library
FROM ${BASE_REGISTRY}/debian:trixie-slim

ARG DEBIAN_FRONTEND=noninteractive

# Use the Aliyun Debian mirror for the sandbox build environment.
RUN printf '%s\n' \
        'Types: deb' \
        'URIs: http://mirrors.aliyun.com/debian' \
        'Suites: trixie trixie-updates' \
        'Components: main' \
        'Signed-By: /usr/share/keyrings/debian-archive-keyring.pgp' \
        '' \
        'Types: deb' \
        'URIs: http://mirrors.aliyun.com/debian-security' \
        'Suites: trixie-security' \
        'Components: main' \
        'Signed-By: /usr/share/keyrings/debian-archive-keyring.pgp' \
        > /etc/apt/sources.list.d/debian.sources

# 系统基础：OpenCode 运行依赖 + Python 运行时。
#   curl/wget  -> webfetch/脚本下载；ripgrep -> opencode grep/glob/skill；
#   procps     -> ps/pkill（超时/取消清理）；jq/coreutils -> run-challenge.sh；
#   git        -> opencode vcs 检测；python3 -> 题目脚本运行时。
# 其余工具（nmap/sqlmap/netcat/nodejs 等）一律不预装：沙箱以 root 运行，
# 由 Agent 在任务中按需 apt-get/pip 安装（apt 源与 pip 源已指向国内镜像）。
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates curl wget git jq \
        ripgrep procps \
        coreutils tar gzip unzip \
        python3 python3-pip python3-venv \
    && rm -rf /var/lib/apt/lists/*

# OpenCode CLI：使用官方 v2 安装器，避免 npm 缓存中的旧版本。
ENV PATH="/root/.opencode/bin:${PATH}"
RUN curl -fsSL https://opencode.ai/v2/install | bash \
    && command -v opencode \
    && opencode --version

# Python 包镜像：预装包已移除，Agent 按需 pip3 install（--break-system-packages）。
RUN pip config set global.index-url https://mirrors.aliyun.com/pypi/simple \
    && pip config set install.trusted-host mirrors.aliyun.com

WORKDIR /workspace
COPY run-challenge.sh /opt/sandbox/run-challenge.sh
COPY sandbox-entry.sh /opt/sandbox/sandbox-entry.sh
COPY AGENTS.md /opt/sandbox/AGENTS.md
RUN chmod +x /opt/sandbox/run-challenge.sh /opt/sandbox/sandbox-entry.sh

ENV FLAG_PATH=/workspace/flag.txt
# 任务全链路（容器入口、exec、opencode serve）以 root 运行，
# Agent 可直接 apt-get/pip 安装缺失工具。
USER root
# 常驻容器：解题脚本由编排器 podman exec 触发，不在启动时自动执行。
ENTRYPOINT ["/opt/sandbox/sandbox-entry.sh"]
