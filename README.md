# AspireCTF — CTF 自动化解题平台

当前 Web UI 使用 MudBlazor，默认登录账号为 `admin` / `admin`。任务信息由 PostgreSQL 持久化保存，包括题目详情、模型、状态、Flag、执行器和 Podman 容器编号。

基于 .NET Aspire + ASP.NET Core Blazor 的 AI 驱动 CTF 解题平台：每个任务在一题一容器的隔离沙箱中运行 [OpenCode CLI](https://opencode.ai)，实时流式回传日志，正则捕获 Flag。

## 架构

```
Blazor Web ──HTTP/SignalR──▶ Aspire API ──队列+并发限流──▶ ChallengeRunner
                                                              │
                                              ┌───────────────┴───────────────┐
                                              ▼                               ▼
                                     podman-api / podman-cli            simulation
                                     （容器内嵌 Podman）              （无沙箱兜底/测试）
                                              │
                                              ▼
                                    ctf-opencode-sandbox 容器
                                    （一题一容器：OpenCode + CTF 工具链）
                                              │
                                         PostgreSQL（任务持久化）
```

- **一题一常驻容器**：每个任务通过 Podman API（容器内嵌 `podman system service`）创建确定性命名的沙箱容器（`ctf-sandbox-{taskId}`）。容器跨次运行持久保留（workspace + OpenCode 会话数据），重复提交同一任务复用容器并恢复会话；**仅删除任务时**才 `podman rm -f` 移除容器并清理 workspace。
- **执行链自动降级**：`podman-api` → `podman-cli` → `simulation`，启动时探测可用执行器。
- **并发限流**：`SemaphoreSlim` 控制最大并发容器数（默认 3，可配 `Sandbox__MaxConcurrent`）。
- **实时日志**：容器 stdout/stderr 流式解析 → SignalR `TaskHub` 推送 + 环形缓冲（供断线重连回放）；沙箱内 `run-challenge.sh` 将 OpenCode SSE 事件结构化输出为 `EVT {json}` 行（text/reasoning/tool/error），API 解析为带 `Part` 的日志记录，Web 端按 OpenCode session-ui 风格渲染（markdown 气泡 / 思考折叠 / 工具卡片含参数与输出）。
- **Flag 捕获**：执行器从宿主侧 workspace 读取 `/workspace/flag.txt`，日志中命中 `FLAG{...}` 仅记录不终止；过滤 `FLAG{xxx}` 类占位符。任务由 OpenCode 自然结束，平台不提前强杀。
- **收尾流程（AGENTS.md）**：`sandbox/AGENTS.md` 随沙箱镜像预置，`run-challenge.sh` 每次运行同步到 `/workspace/`（OpenCode 自动加载项目根此文件）；按流程写 `flag.txt`（成功）/ `failure.txt`（失败）/ `writeup.md`（成败均写）。

## 项目结构

| 目录 | 说明 |
|---|---|
| `AspireCTF.AppHost` | Aspire 编排：PostgreSQL + API + Web（全容器化） |
| `AspireCTF.Api` | 任务队列 / 沙箱执行 / 日志 / REST API / SignalR Hub |
| `AspireCTF.Web` | Blazor Server 前端（任务看板 / 提交 / 实时终端） |
| `AspireCTF.ServiceDefaults` | Aspire 服务默认（健康检查 / OTel / 服务发现） |
| `AspireCTF.Contracts` | API / Web 共享契约：任务与系统 DTO、状态文案（单一来源，避免两端漂移） |
| `sandbox/` | 沙箱镜像（最小化）：debian:trixie-slim + OpenCode CLI + Python 运行时 + 基础依赖（curl/wget/git/jq/ripgrep/procps）；以 **root** 运行，nmap/sqlmap 等其余工具由 Agent 在任务中按需 `apt-get install` / `pip3 install` 安装（apt/pip 已配置国内镜像） |
| `samples/` | 示例题目目录（`Catalog__SamplesPath`） |
| `scripts/` | 镜像构建 / 加载 / 管道验证脚本 |
| `deploy/` | aspirate 生成的 `docker-compose.yaml`、再生成流水线 `generate.ps1`、AI 密钥模板 `.env.example` |

## 快速开始（Aspire，推荐）

前置：Docker Desktop（已启动）、.NET 10 SDK、Node 26（构建前端时）。

```powershell
# 1. 可选：设置 AI Provider 配置。无需设置 OPENAI_API_KEY，任务可以使用免费模型。
# $env:OPENAI_API_KEY = "sk-..."
# 可选：$env:OPENAI_BASE_URL / $env:OPENCODE_MODEL / $env:ANTHROPIC_API_KEY ...

# 2. 启动（AppHost 自动构建 api/web 镜像，首次启动 API 容器内还会构建沙箱镜像，需数分钟）
dotnet run --project AspireCTF.AppHost
```

- Aspire Dashboard：`http://localhost:15213`（`launchSettings.json` 中配置，`ASPIRE_ALLOW_UNSECURED_TRANSPORT=true`）
- API 容器：`--privileged`，内部 `podman system service` 于 `unix:///run/podman/podman.sock`
- Web 容器外网端口由 Aspire 分配（Dashboard 中可见）

### 手动构建镜像（可选，跳过 AppHost 自动构建）

```powershell
docker build -f AspireCTF.Api/Dockerfile -t ctf-api:latest .
docker build -f AspireCTF.Web/Dockerfile -t ctf-web:latest .
# 沙箱镜像：宿主机有 podman 直接构建；否则可由 api 容器首启时入口脚本自动构建
podman build -t ctf-opencode-sandbox:latest sandbox/
```

> 修改 `sandbox/` 脚本后需重建 API 容器内的 Podman 沙箱镜像（`api-entrypoint.sh` 仅在镜像缺失时构建）。手动重建：
> 在 API 容器内执行 `podman rmi ctf-opencode-sandbox:latest` 后重新 build（多源 registry 轮换），并写 `/var/log/ctf-sandbox-build.status`（`ready`/`building` + 日志路径 `/var/log/ctf-sandbox-build.log`）供 `SandboxReadinessService` 读取。`AspireCTF.Api/containers.conf` 已配置 `[build] layers = true` 以启用内层 buildah 分层缓存，后续仅重做变更层。

> **嵌套 cgroup pids 修复**：Docker Desktop 的 cgroup v2 不向子容器暴露 pids 控制器，podman 创建沙箱时若 OCI spec 含 `pids` 资源会触发 `crun: the requested cgroup controller 'pids' is not available`。`pids_limit = 0`（`AspireCTF.Api/containers.conf`）会让 podman 完全省略 pids 控制器；`api-entrypoint.sh` 启动时也会强制改写该值，旧镜像容器重建后自愈。注意 `pids_limit = -1` 无效（仍会触碰控制器）。

## Docker Compose 部署（无 Aspire / .NET SDK）

一键部署（全新服务器 / 任意目录，自动下载仓库到当前目录的 `aspirectf/`）：

```bash
curl -fsSL https://raw.githubusercontent.com/lipeilin2006/aspirectf/main/deploy.sh | sed 's/\r$//' | bash
```

Windows（cmd，下载 `deploy.bat` 到当前目录并运行）：

```bat
curl -fsSL https://raw.githubusercontent.com/lipeilin2006/aspirectf/main/deploy.bat -o deploy.bat && deploy.bat
```

脚本流程：检测 Docker / Git 环境 → 本地无仓库时 `git clone` 到当前目录的 `aspirectf/`（已有则 `git pull` 更新；已在仓库目录内则直接用本地文件）→ 调用 `start` 完成 `.env` 生成、镜像构建与启动。端口（`WEB_PORT` 等）与 AI 密钥在 `deploy/.env` 中配置。

手动启动（已克隆仓库）：

```bash
./start.sh          # 或 start.ps1 / start.bat：构建镜像并启动
./stop.sh           # 或 stop.ps1 / stop.bat：停止（保留数据卷）；--purge / -Purge 连卷删除
```

| 服务 | 宿主端口 |
|---|---|
| web | `http://localhost:8080/login`（默认账号 admin / admin） |
| demo-target（示例靶场） | `http://localhost:8088/login` |
| aspire-dashboard | `http://localhost:18888` |
| postgres | `5432` |
| apiservice | 不对宿主暴露（内网 `8080`；日志 `docker compose -f deploy/docker-compose.yaml logs -f apiservice`） |

- compose 文件在 `deploy/docker-compose.yaml`（由 aspirate 生成，可用 `deploy/generate.ps1` 再生成后重新启动）
- AI Provider 密钥写入 `deploy/.env`（从 `deploy/.env.example` 复制），任务内 AI 模型需自行在平台设置页配置

## 本地开发（无容器，simulation 执行器）

```powershell
$env:ASPNETCORE_URLS='http://localhost:5210'
$env:Sandbox__AllowSimulation='true'
$env:Sandbox__Image='ctf-test-sandbox:latest'   # 可选，scripts/test-image 构造
dotnet run --project AspireCTF.Api
dotnet run --project AspireCTF.Web              # http://localhost:15220
```

## API 摘要

| 方法 | 路径 | 说明 |
|---|---|---|
| POST | `/api/tasks` | 提交任务 `{ name, category?, targetUrl?, challengeId?, model? }` |
| GET | `/api/tasks` | 任务列表 |
| GET | `/api/tasks/{id}` | 任务详情（含 flag / exitCode） |
| GET | `/api/tasks/{id}/logs` | 任务日志回放 |
| DELETE | `/api/tasks/{id}` | 删除任务 |
| POST | `/api/tasks/{id}/cancel` | 取消运行中任务 |
| GET | `/api/system/info` | 执行器 / 队列 / PostgreSQL 状态 |
| GET | `/api/challenges` | 题目目录（`Catalog__SamplesPath`） |
| POST | `/api/tasks/upload` | 上传源码附件（zip） |
| HUB | `/hubs/tasks` | SignalR：`TaskLog` / `TaskStatus` |

## 沙箱执行语义

1. 任务入队 → `ChallengeRunnerService` 出队（并发受 `SemaphoreSlim` 限制）
2. 生成工作目录 `/data/workspaces/{taskId}`（源码解压 + `TASK.md`）
3. 确保容器存在：`ctf-sandbox-{taskId}` 不存在则创建并启动（只读工作目录挂载为 `/workspace`，`--network host`，环境注入 `TARGET_URL` / `FLAG_PATH` / `OPENCODE_CONFIG_CONTENT` 等）；已存在则直接复用（重启恢复 / continue）
4. 若 `/workspace/.opencode-session-id` 存在则 OpenCode 会话恢复继续，否则新建会话
5. 容器内以 exec 方式执行 `/opt/sandbox/run-challenge.sh`：启动 `opencode serve`，全程只经 OpenCode HTTP API 解题（`POST /api/session` 建会话、`POST /api/session/{id}/prompt` 发 Prompt、`GET /api/event` 订阅 SSE 实时事件、`POST /api/session/{id}/interrupt` 中断）
6. SSE 事件经 `EVT {json}` 行结构化输出 → API 转换为带 `Part` 的日志 → 缓冲 + SignalR；日志命中 Flag 仅记录，不提前终止
7. 超时（`Sandbox__TimeoutSeconds`，默认 600s）或 exec 自然退出 → 状态置 `Succeeded` / `Failed`；**容器与 workspace 保留**
8. `DELETE /api/tasks/{id}` → 移除沙箱容器（`podman rm -f`）+ 清理 workspace + 删除任务记录
9. Writeup：`sandbox/AGENTS.md`（收尾流程：flag.txt / failure.txt / writeup.md）随镜像预置并在每次运行时同步到工作区 → Agent 自然结束 → API 读取 `writeup.md` 存入 `ChallengeTask.Writeup` → `GET /tasks/{id}/writeup/download` 可下载

## 配置项（环境变量 / appsettings）

| 键 | 默认 | 说明 |
|---|---|---|
| `Sandbox__Image` | `ctf-opencode-sandbox:latest` | 沙箱镜像 |
| `Sandbox__Endpoint` | `unix:///run/podman/podman.sock` | Podman API 地址（本地开发可用 `podman-cli`） |
| `Sandbox__MaxConcurrent` | `3` | 最大并发容器数 |
| `Sandbox__TimeoutSeconds` | `600` | 单任务超时 |
| `Sandbox__AllowSimulation` | `false` | 是否启用 simulation 兜底 |
| `Sandbox__WorkspacesRoot` | `/data/workspaces`（容器内） | 工作目录根 |
| `ConnectionStrings__ctfdb` | Aspire 注入 | PostgreSQL 任务数据库连接串 |
| `OPENAI_API_KEY` 等 | — | 可选的 AI Provider 配置，宿主→容器→沙箱透传 |
| 任务 `model` | — | Web/API 创建任务时指定，覆盖全局 `OPENCODE_MODEL` |

## 已验证的端到端链路

- Aspire 全容器化启动：`postgres` + `apiservice`（privileged，内嵌 Podman）+ `web` 三容器全部健康
- 容器内嵌 Podman 成功构建沙箱镜像（`ctf-opencode-sandbox:latest`，含依赖预装）
- 提交任务 → 队列 → 容器创建 → OpenCode 运行（HTTP API 全程，无 `opencode run`）→ 结构化日志（text/reasoning/tool/error）流式回传 → Flag 捕获
- 容器持久化：任务完成后容器保留；continue 复用同容器并恢复会话；`DELETE /api/tasks/{id}` 才移除容器 + workspace（未授权返回 403）
- 沙箱以 root 运行（镜像 `USER root`，exec 显式 `User=0`），Agent 可直接 apt/pip 按需安装缺失工具
- Web 端 OpenCode 风格渲染：markdown 气泡、思考折叠、工具卡片（label + 参数 chips + 展开输出 + 错误红卡）、Flag 面板；会话流仅在视口已贴底时自动跟随滚动，上翻阅读不被打断
- 占位符过滤：提示词中的字面量 `FLAG{...}` 不再误报为真 Flag
- Blazor Web 前端 HTTP 200，SignalR Hub 就绪
