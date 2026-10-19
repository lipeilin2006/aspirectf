using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// 配置
// ---------------------------------------------------------------------------
var sandboxImage = builder.Configuration["SandboxImage"] ?? "ctf-opencode-sandbox:latest";

// AI Provider 密钥透传（宿主机环境变量 → API 容器 → 沙箱容器）
string[] passthroughKeys =
[
    "OPENCODE_API_KEY",
    "OPENAI_API_KEY", "OPENAI_BASE_URL",
    "ANTHROPIC_API_KEY", "ANTHROPIC_BASE_URL",
    "GEMINI_API_KEY", "GOOGLE_API_KEY",
    "GROQ_API_KEY", "OPENROUTER_API_KEY", "DEEPSEEK_API_KEY", "MISTRAL_API_KEY",
    "XAI_API_KEY", "COHERE_API_KEY", "TOGETHERAI_API_KEY",
    "OPENCODE_MODEL"
];

// ---------------------------------------------------------------------------
// 1. PostgreSQL：任务状态持久化
// ---------------------------------------------------------------------------
// Keep Aspire's default PostgreSQL tag, but route pulls through a reachable
// registry mirror instead of the default docker.io registry.
var postgres = builder.AddPostgres("postgres")
    .WithImageRegistry("docker.1ms.run")
    .WithDataVolume("ctf-postgres-data");

// Built-in demo target. It is published on the host so the nested sandbox
// can reach it through host.docker.internal when using host networking.
var demoTarget = builder.AddDockerfile("demo-target", "..\\samples\\demo-web-sqli", "Dockerfile")
    .WithHttpEndpoint(port: 8088, targetPort: 8088, name: "http");
var database = postgres.AddDatabase("ctfdb");

// ---------------------------------------------------------------------------
// 2. Orchestrator API（容器化，外层 Docker 运行，容器内嵌 Podman）
//    - --privileged + vfs 存储：容器内运行 podman system service（podman-in-docker）
//    - 每个解题任务动态拉起一个独立沙箱容器（一题一容器）
// ---------------------------------------------------------------------------
var api = builder.AddDockerfile("apiservice", "..", "AspireCTF.Api/Dockerfile")
    .WithHttpEndpoint(targetPort: 8080, name: "http")
    .WithEnvironment("SANDBOX_IMAGE", sandboxImage)
    .WithEnvironment("SANDBOX_BASE_REGISTRY", "docker.1ms.run")
    .WithEnvironment("Sandbox__Image", sandboxImage)
    .WithEnvironment("Sandbox__Provider", "PodmanApi")
    .WithEnvironment("Sandbox__Endpoint", "unix:///run/podman/podman.sock")
    .WithEnvironment("Sandbox__WorkspacesRoot", "/data/workspaces")
    .WithEnvironment("Catalog__SamplesPath", "/opt/catalog")
    .WithVolume("ctf-podman-storage", "/var/lib/containers")
    .WithVolume("ctf-workspaces", "/data/workspaces")
    .WithVolume("ctf-api-keys", "/app/data-protection-keys")
    .WithContainerRuntimeArgs(
        "--privileged",
        "--security-opt", "label=disable",
        "--cgroupns=host",
        "--add-host", "host.docker.internal:host-gateway")
    .WithReference(database)
    .WaitFor(database)
    .WaitFor(demoTarget);

foreach (var key in passthroughKeys)
{
    var value = Environment.GetEnvironmentVariable(key);
    if (!string.IsNullOrWhiteSpace(value))
    {
        api = api.WithEnvironment(key, value);
    }
}

// ---------------------------------------------------------------------------
// 3. Blazor 前端（容器化）：任务看板 / 提交面板 / 实时日志终端
// ---------------------------------------------------------------------------
var web = builder.AddDockerfile("web", "..", "AspireCTF.Web/Dockerfile")
    .WithExternalHttpEndpoints()
    .WithHttpEndpoint(targetPort: 8080, name: "http")
    .WithEnvironment("ApiBaseUrl", api.GetEndpoint("http"))
    .WithReference(database)
    .WaitFor(api);

builder.Build().Run();
