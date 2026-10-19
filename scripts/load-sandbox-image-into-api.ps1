# 将宿主机上的沙箱镜像载入到 Aspire API 容器内的嵌套 podman（podman-in-docker）中，
# 避免每次 API 容器启动时在嵌套 podman 里重复构建镜像。
# 用法: 先启动 Aspire（api 容器在运行），再执行本脚本。
param(
    [string]$Tag = "ctf-opencode-sandbox:latest",
    [string]$ApiContainer = "apiservice"
)

$ErrorActionPreference = "Stop"

$save = Join-Path $env:TEMP "sandbox-image.tar"
docker save -o $save $Tag
if ($LASTEXITCODE -ne 0) { throw "docker save failed" }

docker cp $save "${ApiContainer}:/tmp/sandbox.tar"
docker exec $ApiContainer sh -c "podman load -i /tmp/sandbox.tar && rm -f /tmp/sandbox.tar"

Remove-Item $save -Force -ErrorAction SilentlyContinue
Write-Host "image loaded into ${ApiContainer}: $Tag"
