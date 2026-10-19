using System.Text.Json;
using AspireCTF.Api.Sandbox;

namespace AspireCTF.Api.Runner;

/// <summary>Converts EVT JSON lines into compact, structured log summaries.</summary>
public static class SandboxLogEventParser
{
    public static SandboxLogEvent Parse(SandboxLogEvent line)
    {
        if (line.Part is not null || !line.Text.StartsWith("EVT ", StringComparison.Ordinal))
            return line;

        var json = line.Text[4..].Trim();
        if (json.Length == 0)
            return line;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var kind = GetString(root, "k");
            var summary = kind switch
            {
                "text" => $"[assistant] {Truncate(GetString(root, "text"))}",
                "reasoning" => $"[思考] {Truncate(GetString(root, "text"))}",
                "tool" => BuildToolSummary(root),
                "error" => $"[error] {GetString(root, "err")}",
                _ => null
            };

            return summary is null ? line : new SandboxLogEvent(line.Stream, summary, line.At, json);
        }
        catch (JsonException)
        {
            return line;
        }
    }

    private static string GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string Truncate(string text)
    {
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length <= 120 ? text : text[..120] + "…";
    }

    private static string BuildToolSummary(JsonElement root)
    {
        var tool = GetString(root, "tool");
        var status = GetString(root, "status");
        var label = GetString(root, "label");
        var error = GetString(root, "err");
        var head = string.IsNullOrWhiteSpace(label) ? tool : $"{tool}: {label}";

        return status switch
        {
            "done" => $"[tool] {Truncate(head)} ✓",
            "error" => $"[tool] {Truncate(head)} ✗ {Truncate(error)}",
            _ => $"[tool] {Truncate(head)} …"
        };
    }
}
