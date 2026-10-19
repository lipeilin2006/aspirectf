# 构建 CTF 沙箱镜像（opencode + 工具链）到宿主机容器引擎（docker/podman 均可）
param(
    [string]$Tag = "ctf-opencode-sandbox:latest",
    [ValidateSet("docker", "podman", "auto")]
    [string]$Engine = "auto"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if ($Engine -eq "auto") {
    if (Get-Command docker -ErrorAction SilentlyContinue) { $Engine = "docker" }
    elseif (Get-Command podman -ErrorAction SilentlyContinue) { $Engine = "podman" }
    else { throw "no container engine found (docker/podman)" }
}

& $Engine build -t $Tag (Join-Path $root "sandbox")
if ($LASTEXITCODE -ne 0) { throw "sandbox image build failed" }
Write-Host "sandbox image built: $Tag (engine: $Engine)"
