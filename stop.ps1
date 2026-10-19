# AspireCTF 一键停止
param([switch]$Purge)   # -Purge 同时删除数据卷（危险）
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if ($Purge) {
    docker compose -f deploy\docker-compose.yaml down -v
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Host '已停止并清除数据卷。'
} else {
    docker compose -f deploy\docker-compose.yaml down
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Host '已停止（数据卷保留；重新 .\start.ps1 后任务仍在）。'
}
