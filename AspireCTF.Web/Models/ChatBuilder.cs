using System.Text.Json;
using System.Text.RegularExpressions;
using Markdig;

namespace AspireCTF.Web.Models;

/// <summary>One structured chat item derived from a sandbox log Part (or a legacy flat line).</summary>
public sealed class ChatItem
{
    public required string Kind { get; init; } // text | reasoning | tool | error | sandbox | prompt | raw
    public string Id { get; init; } = "";
    public string Status { get; set; } = "";
    public string Tool { get; init; } = "";
    public string Label { get; set; } = "";
    public IReadOnlyList<string> Args { get; set; } = [];
    public string Text { get; set; } = "";
    public string Err { get; set; } = "";
    public string Stream { get; init; } = "Stdout";
    public long Seq { get; init; }
    /// <summary>sandbox 种类：折叠组内的原始行数。</summary>
    public int LineCount { get; set; }
}

/// <summary>
/// 将 API 返回的结构化 Part（run-challenge.sh 的 `EVT {json}`）合并为按官方
/// session-ui 模型渲染的聊天条目：text/reasoning 按 id 累积，tool 按 id
/// 状态流转（running → done/error）。
/// </summary>
public static class ChatBuilder
{
    // 刻意不用 UseAdvancedExtensions：其内联属性列表（{...}）会把
    // `FLAG{...}` 之类的内容渲染成 HTML 属性，既破坏显示也构成注入面。
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseAutoLinks()
        .DisableHtml()
        .Build();

    private static readonly Regex OnAttributeRegex = new(
        @"\son[a-z]+\s*=\s*""[^""]*""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HrefRegex = new(
        @"href\s*=\s*""([^""]*)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string ToMarkdownHtml(string text)
    {
        var html = Markdown.ToHtml(text ?? "", Pipeline);
        html = OnAttributeRegex.Replace(html, "");
        return HrefRegex.Replace(html, static match =>
        {
            var url = match.Groups[1].Value.Trim();
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith('#')
                || url.StartsWith('/'))
            {
                return match.Value;
            }

            return "href=\"#\"";
        });
    }

    public static List<ChatItem> Build(IEnumerable<LogLineDto> lines)
    {
        var items = new List<ChatItem>();
        var index = new Dictionary<string, ChatItem>();
        // Agent 输出前的一切原始 shell 行（[sandbox] 自检、ls 列表、分隔线等）
        // 都属于启动噪声，收进折叠分组；Agent 开始后仅按前缀识别。
        var agentStarted = false;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line.Part))
            {
                if (IsMetaLine(line.Text) || !agentStarted)
                {
                    if (items.Count > 0 && items[^1].Kind == "sandbox")
                    {
                        items[^1].Text += "\n" + line.Text;
                        items[^1].LineCount++;
                    }
                    else
                    {
                        items.Add(new ChatItem
                        {
                            Kind = "sandbox",
                            Text = line.Text,
                            LineCount = 1,
                            Stream = line.Stream,
                            Seq = line.Seq
                        });
                    }
                    continue;
                }

                items.Add(new ChatItem { Kind = "raw", Text = line.Text, Stream = line.Stream, Seq = line.Seq });
                continue;
            }

            agentStarted = true;

            ChatPart? part;
            try
            {
                part = JsonSerializer.Deserialize<ChatPart>(line.Part, Json.Options);
            }
            catch (JsonException)
            {
                items.Add(new ChatItem { Kind = "raw", Text = line.Text, Stream = line.Stream, Seq = line.Seq });
                continue;
            }

            if (part is null)
            {
                items.Add(new ChatItem { Kind = "raw", Text = line.Text, Stream = line.Stream, Seq = line.Seq });
                continue;
            }

            switch (part.K)
            {
                case "error":
                    items.Add(new ChatItem { Kind = "error", Err = part.Err ?? line.Text, Seq = line.Seq, Stream = line.Stream });
                    break;

                case "prompt":
                    // Runner 在执行前发布的实际输入 Prompt，折叠卡片展示。
                    items.Add(new ChatItem { Kind = "prompt", Text = part.Text ?? "", Seq = line.Seq, Stream = line.Stream });
                    break;

                case "tool":
                    ChatItem? toolItem;
                    if (!string.IsNullOrEmpty(part.Id) && index.TryGetValue($"tool:{part.Id}", out var existing))
                    {
                        toolItem = existing;
                        toolItem.Text = string.IsNullOrEmpty(part.Text) ? toolItem.Text : part.Text;
                        toolItem.Err = string.IsNullOrEmpty(part.Err) ? toolItem.Err : part.Err;
                    }
                    else
                    {
                        toolItem = new ChatItem
                        {
                            Kind = "tool",
                            Id = part.Id ?? "",
                            Tool = part.Tool ?? "tool",
                            Label = part.Label ?? "",
                            Args = part.Args ?? [],
                            Text = part.Text ?? "",
                            Err = part.Err ?? "",
                            Status = part.Status ?? "running",
                            Seq = line.Seq,
                            Stream = line.Stream
                        };
                        items.Add(toolItem);
                        if (!string.IsNullOrEmpty(part.Id))
                        {
                            index[$"tool:{part.Id}"] = toolItem;
                        }
                    }

                    // Status transitions to the latest non-empty value.
                    if (!string.IsNullOrEmpty(part.Status))
                    {
                        toolItem.Status = part.Status;
                    }

                    // Later input.ended events carry label/args the first started event lacks.
                    if (!string.IsNullOrEmpty(part.Label))
                    {
                        toolItem.Label = part.Label;
                    }

                    if (part.Args is { Count: > 0 })
                    {
                        toolItem.Args = part.Args;
                    }

                    break;

                case "reasoning":
                case "text":
                    var key = $"{part.K}:{part.Id}";
                    ChatItem? textItem;
                    if (!string.IsNullOrEmpty(part.Id) && index.TryGetValue(key, out var textExisting))
                    {
                        textItem = textExisting;
                    }
                    else
                    {
                        textItem = new ChatItem
                        {
                            Kind = part.K!,
                            Id = part.Id ?? "",
                            Seq = line.Seq,
                            Stream = line.Stream
                        };
                        items.Add(textItem);
                        if (!string.IsNullOrEmpty(part.Id))
                        {
                            index[key] = textItem;
                        }
                    }

                    // done events carry the full accumulated text and replace
                    // any streamed running fragments; running deltas append.
                    if (part.Status == "done")
                    {
                        textItem.Text = part.Text ?? textItem.Text;
                    }
                    else
                    {
                        textItem.Text += part.Text ?? "";
                    }

                    if (!string.IsNullOrEmpty(part.Status))
                    {
                        textItem.Status = part.Status;
                    }

                    break;

                default:
                    items.Add(new ChatItem { Kind = "raw", Text = part.Text ?? line.Text, Stream = line.Stream, Seq = line.Seq });
                    break;
            }
        }

        // Drop empty shells (e.g. tool start events superseded by nothing).
        return items.Where(i => i.Kind != "tool" || !string.IsNullOrEmpty(i.Label) || !string.IsNullOrEmpty(i.Text) || i.Status == "error").ToList();
    }

    private static bool IsMetaLine(string text)
        => text.StartsWith("[sandbox]", StringComparison.Ordinal)
           || text.StartsWith("[executor]", StringComparison.Ordinal)
           || text.StartsWith("[opencode]", StringComparison.Ordinal);

    private sealed class Json
    {
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    }

    private sealed class ChatPart
    {
        public string? K { get; set; }
        public string? Id { get; set; }
        public string? Tool { get; set; }
        public string? Status { get; set; }
        public string? Label { get; set; }
        public List<string>? Args { get; set; }
        public string? Text { get; set; }
        public string? Err { get; set; }
    }
}
