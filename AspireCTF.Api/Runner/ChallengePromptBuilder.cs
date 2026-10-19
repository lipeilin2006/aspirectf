using AspireCTF.Api.Models;
using AspireCTF.Api.Sandbox;
using Microsoft.Extensions.Options;

namespace AspireCTF.Api.Runner;

/// <summary>Builds the effective prompt sent to OpenCode for a task.</summary>
public sealed class ChallengePromptBuilder(IOptions<SandboxOptions> options)
{
    private readonly string _template = options.Value.PromptTemplate;

    public string Build(ChallengeTask task)
    {
        var template = string.IsNullOrWhiteSpace(task.Prompt) ? _template : task.Prompt;
        var descriptionEmbedded = template.Contains("{3", StringComparison.Ordinal);
        string prompt;

        try
        {
            prompt = string.Format(template, task.Name, task.Category, task.TargetUrl, task.Description);
        }
        catch (FormatException)
        {
            descriptionEmbedded = true;
            try
            {
                prompt = string.Format(_template, task.Name, task.Category, task.TargetUrl, task.Description);
            }
            catch (FormatException)
            {
                prompt = $"{_template}\n\n【题目详情】\n{task.Description}";
            }
        }

        if (!descriptionEmbedded && !string.IsNullOrWhiteSpace(task.Description))
            prompt += $"\n\n【题目详情】\n{task.Description}";

        if (!string.IsNullOrWhiteSpace(task.AdditionalPrompt))
            prompt += $"\n\n【追加信息】\n{task.AdditionalPrompt.Trim()}";

        return prompt;
    }
}
