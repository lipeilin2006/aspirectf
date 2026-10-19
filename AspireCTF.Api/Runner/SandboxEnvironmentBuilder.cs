using System.Globalization;
using AspireCTF.Api.Models;
using AspireCTF.Api.Sandbox;
using AspireCTF.Api.Services;
using Microsoft.Extensions.Options;

namespace AspireCTF.Api.Runner;

/// <summary>Builds the environment passed to a task sandbox.</summary>
public sealed class SandboxEnvironmentBuilder(
    IOptions<SandboxOptions> options,
    PlatformSettingsService platformSettings)
{
    private readonly SandboxOptions _options = options.Value;

    public Dictionary<string, string> Build(ChallengeTask task, string prompt, int servePort)
    {
        var env = new Dictionary<string, string>
        {
            ["TASK_ID"] = task.Id,
            ["TASK_NAME"] = task.Name,
            ["TARGET_URL"] = task.TargetUrl,
            ["PROMPT"] = prompt,
            ["FLAG_PATH"] = "/workspace/flag.txt",
            ["OPENCODE_SERVE_PORT"] = servePort.ToString(CultureInfo.InvariantCulture)
        };

        var config = Environment.GetEnvironmentVariable("OPENCODE_CONFIG_JSON");
        if (!string.IsNullOrWhiteSpace(config))
            env["OPENCODE_CONFIG_CONTENT"] = config;

        var passthrough = new HashSet<string>(_options.PassthroughEnvKeys, StringComparer.OrdinalIgnoreCase);
        passthrough.UnionWith(platformSettings.ConfiguredEnvVarNames);
        foreach (var key in passthrough)
            AddIfConfigured(env, key);

        if (!string.IsNullOrWhiteSpace(task.Model))
            env["OPENCODE_MODEL"] = task.Model.Trim();

        if (!string.IsNullOrWhiteSpace(task.SessionId))
            env["OPENCODE_SESSION_ID"] = task.SessionId;

        env["OPENCODE_TIMEOUT_SECONDS"] = Math.Max(0, _options.OpenCodeTimeoutSeconds).ToString(CultureInfo.InvariantCulture);
        return env;
    }

    private static void AddIfConfigured(IDictionary<string, string> env, string key)
    {
        var value = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(value))
            env[key] = value;
    }
}
