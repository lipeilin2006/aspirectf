# 构建上下文为仓库根目录： podman build -f AspireCTF.Api/Dockerfile .

# ---------------------------- build stage ----------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY AspireCTF.Contracts/AspireCTF.Contracts.csproj AspireCTF.Contracts/
COPY AspireCTF.ServiceDefaults/AspireCTF.ServiceDefaults.csproj AspireCTF.ServiceDefaults/
COPY AspireCTF.Api/AspireCTF.Api.csproj AspireCTF.Api/
RUN dotnet restore AspireCTF.Api/AspireCTF.Api.csproj

COPY AspireCTF.Contracts/ AspireCTF.Contracts/
COPY AspireCTF.ServiceDefaults/ AspireCTF.ServiceDefaults/
COPY AspireCTF.Api/ AspireCTF.Api/
RUN dotnet publish AspireCTF.Api/AspireCTF.Api.csproj -c Release -o /app/publish /p:UseAppHost=false /p:DebugType=none

# ---------------------------- runtime stage ---------------------------
# 运行时镜像内置 podman：API 容器以 --privileged 启动，
# 入口脚本拉起 podman system service，实现 podman-in-podman 执行层。
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final

RUN sed -i \
        -e 's@archive.ubuntu.com/ubuntu@mirrors.aliyun.com/ubuntu@g' \
        -e 's@security.ubuntu.com/ubuntu@mirrors.aliyun.com/ubuntu@g' \
        /etc/apt/sources.list.d/ubuntu.sources

RUN apt-get update && apt-get install -y --no-install-recommends \
        podman crun slirp4netns uidmap catatonit \
         curl ca-certificates procps \
    && rm -rf /var/lib/apt/lists/*

# OpenCode CLI is also available in the API container for model discovery and
# configuration checks. Use the official v2 installer rather than npm.
ENV PATH="/root/.opencode/bin:${PATH}"
RUN curl -fsSL https://opencode.ai/v2/install | bash \
    && command -v opencode \
    && opencode --version

# 嵌套 podman 配置：vfs 存储驱动 + cgroupfs（容器内无 systemd）
RUN mkdir -p /etc/containers
COPY AspireCTF.Api/containers.conf /etc/containers/containers.conf
COPY AspireCTF.Api/storage.conf /etc/containers/storage.conf

WORKDIR /app
COPY --from=build /app/publish .
COPY sandbox/ /opt/sandbox/
COPY samples/ /opt/catalog/
COPY AspireCTF.Api/api-entrypoint.sh /opt/api-entrypoint.sh
RUN chmod +x /opt/api-entrypoint.sh \
    && mkdir -p /data/workspaces /run/podman

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0 \
    SANDBOX__ENDPOINT=unix:///run/podman/podman.sock \
    SANDBOX__WORKSPACESROOT=/data/workspaces \
    SANDBOX__IMAGE=ctf-opencode-sandbox:latest \
    SANDBOX_IMAGE=ctf-opencode-sandbox:latest \
    Catalog__SamplesPath=/opt/catalog

EXPOSE 8080
ENTRYPOINT ["/opt/api-entrypoint.sh"]
