# AspireCTF 一键启动（docker compose，compose 文件在 deploy\ 下）
param([switch]$Build = $true)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not (Test-Path 'deploy\.env')) { Copy-Item 'deploy\.env.example' 'deploy\.env' }

$composeArgs = @('compose', '-f', 'deploy\docker-compose.yaml', 'up', '-d')
if ($Build) { $composeArgs += '--build' }
docker @composeArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ''
Write-Host 'AspireCTF 已启动:'
Write-Host '  Web       http://localhost:8080'
Write-Host '  Demo 靶场 http://localhost:8088/login'
Write-Host '  Dashboard http://localhost:18888'
Write-Host ''
Write-Host '提示: 首次解题时沙箱会在容器内构建 ctf-opencode-sandbox 镜像（约 1-2 分钟）。'
