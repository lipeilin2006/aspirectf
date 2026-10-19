namespace AspireCTF.Api.Sandbox;

public sealed class SandboxOptions
{
    public const string SectionName = "Sandbox";

    /// <summary>dockerapi | podmanapi | api | podmancli | cli | simulation | sim；其他值 = 自动降级链</summary>
    public string Provider { get; set; } = "PodmanApi";

    public string Image { get; set; } = "ctf-opencode-sandbox:latest";

    /// <summary>Override engine API endpoint, e.g. unix:///run/podman/podman.sock</summary>
    public string? Endpoint { get; set; }

    public int MaxConcurrent { get; set; } = 3;

    public int MaxQueueLength { get; set; } = 64;

    public int TaskTimeoutMinutes { get; set; } = 20;
    public int OpenCodeTimeoutSeconds { get; set; }

    public string WorkspacesRoot { get; set; } = Path.Combine(AppContext.BaseDirectory, "workspaces");

    public bool KeepWorkspaceOnFinish { get; set; }

    public bool AllowSimulation { get; set; }

    /// <summary>Flag used only by the Simulation executor for end-to-end pipeline tests.</summary>
    public string SimulatedFlag { get; set; } = "FLAG{simulation_pipeline_ok}";

    /// <summary>内置默认 Prompt 模板。占位符：{0}=任务名 {1}=类别 {2}=靶场地址 {3}=题目详情；字面大括号需写成 {{ }}。</summary>
    public string PromptTemplate { get; set; } = """
        你是资深 CTF 选手，负责自动解题。题目文件已解压在当前工作目录（容器内 /workspace）。

        【必须遵守 /workspace/AGENTS.md】
        /workspace/AGENTS.md 定义了收尾流程，你必须严格遵守：
        - 先读一遍 /workspace/AGENTS.md，按其中规定写好 flag.txt / failure.txt / writeup.md；
        - 不得跳过或自行发明其他收尾方式，一切以 AGENTS.md 为准；
        - 平台不会中途强制结束会话，你按 AGENTS.md 自然完成即可。

        【目标】
        - 题目：{0}（类别 {1}）
        - 靶场：{2}

        【题目详情】
        {3}

        【解题步骤】
        1. 先审计 /workspace 下的源码与 TASK.md，理解业务逻辑并定位漏洞；不要盲目扫端口。
        2. 按需安装工具：本环境以 root 运行，可直接 apt-get install / pip3 install --break-system-packages
           （apt/pip 均已配置国内镜像）；优先复用源码已有依赖，避免装重型工具浪费时间。
        3. 编写 Python 脚本（保存为 /workspace/exp.py）利用漏洞攻击靶场，提取 flag。

        【输出要求】
        - 过程中用简短中文说明关键发现；最终给出一段结论（漏洞成因 + 利用方式 + flag）。
        - 收尾前再次确认已按 /workspace/AGENTS.md 写好 flag.txt / failure.txt / writeup.md。
        - 全部完成后输出 DONE。
        """;

    public string[] PassthroughEnvKeys { get; set; } =
    [
        "OPENCODE_API_KEY",
        "OPENAI_API_KEY",
        "OPENAI_BASE_URL",
        "ANTHROPIC_API_KEY",
        "ANTHROPIC_BASE_URL",
        "GEMINI_API_KEY",
        "GOOGLE_API_KEY",
        "GROQ_API_KEY",
        "OPENROUTER_API_KEY",
        "DEEPSEEK_API_KEY",
        "MISTRAL_API_KEY",
        "XAI_API_KEY",
        "COHERE_API_KEY",
        "TOGETHERAI_API_KEY",
        "OPENCODE_MODEL",
        "OPENCODE_THEME"
    ];
}
