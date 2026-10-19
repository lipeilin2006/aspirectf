# 重新生成 docker-compose.yaml（Aspirate 流水线）
#   1. 运行 AppHost 生成 Aspire manifest（Aspire CLI run 模式不会自动退出 → 轮询生成后强制结束）
#   2. aspirate generate 生成 compose（compose-build 资源交给 docker compose 本地构建）
#   3. 修补 compose：相对构建路径 / --privileged / 端口冲突 / depends_on / 健康检查 / POSTGRES_DB / AI 密钥 env_file
param(
    [string]$OutputPath = $PSScriptRoot,
    [string]$ManifestPath = (Join-Path $PSScriptRoot '..\AspireCTF.AppHost\manifest.json'),
    [switch]$SkipManifest
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$appHost = Join-Path $root 'AspireCTF.AppHost'
$env:DOTNET_ROLL_FORWARD = 'Major'

if (-not $SkipManifest) {
    Write-Host '[1/3] generating Aspire manifest...'
    if (Test-Path $ManifestPath) { Remove-Item $ManifestPath -Force }
    $p = Start-Process dotnet -ArgumentList @('run', '--project', $appHost, '--', '--publisher', 'manifest', '--output-path', $ManifestPath) `
        -WorkingDirectory $appHost -RedirectStandardOutput "$env:TEMP\aspirate-manifest.out" `
        -RedirectStandardError "$env:TEMP\aspirate-manifest.err" -WindowStyle Hidden -PassThru
    $deadline = (Get-Date).AddMinutes(5)
    while ((Get-Date) -lt $deadline) {
        if ((Test-Path $ManifestPath) -and ((Get-Item $ManifestPath).LastWriteTime -gt (Get-Date).AddMinutes(-1))) { break }
        if ($p.HasExited) { break }
        Start-Sleep -Milliseconds 500
    }
    Start-Sleep -Seconds 2
    # Aspire CLI run 会话常驻：manifest 已写出后直接结束进程树
    & taskkill /PID $p.Id /T /F 2>$null | Out-Null
    if (-not (Test-Path $ManifestPath)) { throw "manifest generation failed: $ManifestPath not created" }
    Write-Host "      manifest: $ManifestPath"
}

Write-Host '[2/3] aspirate generate (compose)...'
$aspLog = "$env:TEMP\aspirate-generate.log"
$aspErr = "$env:TEMP\aspirate-generate.err"
$ap = Start-Process aspirate -ArgumentList @(
    'generate', '--non-interactive', '--disable-secrets', '--skip-build', '--with-dashboard',
    '--output-format', 'compose', '--output-path', $OutputPath, '--prefer-dockerfile',
    '--compose-build', 'apiservice', '--compose-build', 'web', '--compose-build', 'demo-target',
    '--project-path', $appHost, '-m', $ManifestPath
) -WorkingDirectory $appHost -RedirectStandardOutput $aspLog -RedirectStandardError $aspErr -WindowStyle Hidden -PassThru -Wait
if ($ap.ExitCode -ne 0) {
    Get-Content $aspLog -Tail 30 -ErrorAction SilentlyContinue
    Get-Content $aspErr -Tail 20 -ErrorAction SilentlyContinue
    throw "aspirate generate failed: exit $($ap.ExitCode)"
}

Write-Host '[3/3] patching compose file...'
$composePath = Join-Path $OutputPath 'docker-compose.yaml'
$text = Get-Content $composePath -Raw

# --- 构建路径改为相对 compose 文件（跨平台可部署） ---
$text = $text -replace 'context: "D:\\\\AspireCTF\\\\samples\\\\demo-web-sqli"', 'context: "../samples/demo-web-sqli"'
$text = $text -replace 'dockerfile: "D:\\\\AspireCTF\\\\samples\\\\demo-web-sqli\\\\Dockerfile"', 'dockerfile: "Dockerfile"'
$text = $text -replace 'context: "D:\\\\AspireCTF"', 'context: ".."'
$text = $text -replace 'dockerfile: "D:\\\\AspireCTF\\\\AspireCTF\.Api\\\\Dockerfile"', 'dockerfile: "AspireCTF.Api/Dockerfile"'
$text = $text -replace 'dockerfile: "D:\\\\AspireCTF\\\\AspireCTF\.Web\\\\Dockerfile"', 'dockerfile: "AspireCTF.Web/Dockerfile"'

# --- 删除未替换的参数占位符变量（应用只用 ConnectionStrings__ctfdb） ---
$text = ($text -split "`n" | Where-Object { $_ -notmatch 'CTFDB_URI:' -and $_ -notmatch 'CTFDB_JDBCCONNECTIONSTRING:' }) -join "`n"

# --- postgres：建库 + 健康检查（对应 Aspire 的 WaitFor(database)） ---
$text = $text -replace '(?m)^      POSTGRES_USER: "postgres"$', "      POSTGRES_USER: `"postgres`"`n      POSTGRES_DB: `"ctfdb`""

# postgres 服务块：在 ports 前插入 healthcheck（注意：块内用 .*? 线性匹配，(?:.*\r?\n)*? 会组合爆炸挂死）
$text = $text -replace '(?ms)(  postgres:\r?\n.*?)(    ports:\r?\n    - target: 5432)', "`$1    healthcheck:`n      test: [`"CMD-SHELL`", `"pg_isready -U postgres -d ctfdb`"]`n      interval: 5s`n      timeout: 5s`n      retries: 12`n`$2"

# --- apiservice：移除宿主端口（web 走内部网络），补齐 podman-in-docker 运行参数与依赖 ---
$text = $text -replace '(?ms)(  apiservice:\r?\n.*?)(    ports:\r?\n    - target: 8080\r?\n      published: 8080\r?\n)(    restart: unless-stopped)', @"
`$1    privileged: true
    security_opt:
      - label=disable
    extra_hosts:
      - `"host.docker.internal:host-gateway`"
    env_file:
      - .env
    depends_on:
      postgres:
        condition: service_healthy
      demo-target:
        condition: service_started
`$3
"@

# --- web：依赖 apiservice ---
$text = $text -replace '(?ms)(  web:\r?\n.*?)(    restart: unless-stopped)', @"
`$1    depends_on:
      apiservice:
        condition: service_started
`$2
"@

[System.IO.File]::WriteAllText($composePath, $text, (New-Object System.Text.UTF8Encoding $false))
Write-Host "      patched: $composePath"
Write-Host 'done.'
