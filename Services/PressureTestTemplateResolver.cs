using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DubboTester.Services;

public static class PressureTestTemplateResolver
{
    private static readonly Regex PlaceholderPattern = new(
        @"\{\{\s*(?<expression>[^{}]+?)\s*\}\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const string Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    public static string Resolve(string template, int userIndex, int iterationIndex)
    {
        if (template is null) throw new ArgumentNullException(nameof(template));
        if (userIndex < 1) throw new ArgumentOutOfRangeException(nameof(userIndex));
        if (iterationIndex < 1) throw new ArgumentOutOfRangeException(nameof(iterationIndex));

        return PlaceholderPattern.Replace(template, match =>
            ResolveExpression(match.Groups["expression"].Value.Trim(), userIndex, iterationIndex));
    }

    public static string FormatJsonTemplate(string template)
    {
        if (template is null) throw new ArgumentNullException(nameof(template));

        var masked = new StringBuilder(template.Length);
        var variables = new List<(string Sentinel, string Placeholder, bool WasInsideString)>();
        bool insideString = false;
        bool escaped = false;

        for (int index = 0; index < template.Length;)
        {
            if (index + 1 < template.Length && template[index] == '{' && template[index + 1] == '{')
            {
                int end = template.IndexOf("}}", index + 2, StringComparison.Ordinal);
                if (end < 0) throw new FormatException("参数模板中存在未闭合的变量。");

                string placeholder = template[index..(end + 2)];
                _ = Resolve(placeholder, 1, 1);
                string sentinel = $"__DUBBO_TEMPLATE_{variables.Count:D4}__";
                variables.Add((sentinel, placeholder, insideString));
                if (insideString) masked.Append(sentinel);
                else masked.Append('"').Append(sentinel).Append('"');
                index = end + 2;
                escaped = false;
                continue;
            }

            char current = template[index++];
            masked.Append(current);
            if (current == '"' && !escaped) insideString = !insideString;
            if (insideString && current == '\\') escaped = !escaped;
            else escaped = false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(masked.ToString());
        }
        catch (JsonException)
        {
            document = JsonDocument.Parse($"[{masked}]");
        }

        using (document)
        {
            string formatted = JsonSerializer.Serialize(
                document.RootElement,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
            foreach ((string sentinel, string placeholder, bool wasInsideString) in variables)
            {
                formatted = wasInsideString
                    ? formatted.Replace(sentinel, placeholder, StringComparison.Ordinal)
                    : formatted.Replace($"\"{sentinel}\"", placeholder, StringComparison.Ordinal);
            }

            return formatted;
        }
    }

    private static string ResolveExpression(string expression, int userIndex, int iterationIndex)
    {
        string[] parts = expression.Split(':', StringSplitOptions.TrimEntries);
        string name = parts[0].ToLowerInvariant();
        return name switch
        {
            "user" or "userindex" when parts.Length == 1 =>
                userIndex.ToString(CultureInfo.InvariantCulture),
            "iteration" or "iterationindex" when parts.Length == 1 =>
                iterationIndex.ToString(CultureInfo.InvariantCulture),
            "guid" when parts.Length == 1 => Guid.NewGuid().ToString("N"),
            "timestamp" when parts.Length == 1 => DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            "unixtimestamp" when parts.Length == 1 =>
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            "randomint" => RandomInteger(parts),
            "randomstring" => RandomString(parts),
            _ => throw new FormatException($"不支持的压测变量：{{{{{expression}}}}}。")
        };
    }

    private static string RandomInteger(string[] parts)
    {
        int minimum = 0;
        int maximum = 999_999;
        if (parts.Length == 3
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedMinimum)
            && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedMaximum))
        {
            minimum = parsedMinimum;
            maximum = parsedMaximum;
        }
        else if (parts.Length != 1)
        {
            throw new FormatException("randomInt 格式应为 {{randomInt}} 或 {{randomInt:最小值:最大值}}。");
        }

        if (minimum > maximum) throw new FormatException("randomInt 的最小值不能大于最大值。");
        long exclusiveMaximum = (long)maximum + 1;
        return Random.Shared.NextInt64(minimum, exclusiveMaximum).ToString(CultureInfo.InvariantCulture);
    }

    private static string RandomString(string[] parts)
    {
        int length = 8;
        if (parts.Length == 2
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedLength))
        {
            length = parsedLength;
        }
        else if (parts.Length != 1)
        {
            throw new FormatException("randomString 格式应为 {{randomString}} 或 {{randomString:长度}}。");
        }

        if (length is < 1 or > 256) throw new FormatException("randomString 长度必须在 1 到 256 之间。");
        var result = new StringBuilder(length);
        for (int index = 0; index < length; index++)
        {
            result.Append(Alphabet[Random.Shared.Next(Alphabet.Length)]);
        }
        return result.ToString();
    }
}
