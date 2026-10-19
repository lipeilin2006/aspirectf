using System.Text.RegularExpressions;

namespace AspireCTF.Api.Services;

public static partial class FlagExtractor
{
    // flag/ctf/picoCTF{...}，body 至少 3 字符
    [GeneratedRegex(@"\b(?:flag|ctf|picoCTF|FLAG|Flag|CTF)\{([^{}\r\n]{3,256})\}", RegexOptions.Compiled)]
    private static partial Regex FlagRegex();

    // 疑似占位符的 body：全是 . * x _ - < > 空格等，或含明显占位语义
    private static readonly Regex PlaceholderBody = new(@"^[\.\*_\-xXyY<> ]+$", RegexOptions.Compiled);

    public static string? Extract(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (Match match in FlagRegex().Matches(text))
        {
            if (IsRealFlag(match))
            {
                return match.Value;
            }
        }

        return null;
    }

    public static IEnumerable<string> ExtractAll(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        foreach (Match match in FlagRegex().Matches(text))
        {
            if (IsRealFlag(match))
            {
                yield return match.Value;
            }
        }
    }

    private static bool IsRealFlag(Match match)
    {
        var body = match.Groups[1].Value;
        if (PlaceholderBody.IsMatch(body))
        {
            return false; // FLAG{...} / FLAG{xxxx} / FLAG{your_flag_here} 类占位符
        }

        return true;
    }
}
