using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DubboTester.Services;

public static class JsonArgumentParser
{
    private static readonly JsonSerializerOptions DisplayJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static object?[] ParseArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "[]")
        {
            return [];
        }

        string source = json.Trim();
        try
        {
            using JsonDocument document = JsonDocument.Parse(source);
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().Select(ConvertElement).ToArray()
                : [ConvertElement(document.RootElement)];
        }
        catch (JsonException)
        {
            using JsonDocument wrapped = JsonDocument.Parse($"[{source}]");
            return wrapped.RootElement.EnumerateArray().Select(ConvertElement).ToArray();
        }
    }

    public static string[] ParseJavaTypes(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    public static IReadOnlyDictionary<string, string>? ParseAttachments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("Attachments 必须是 JSON 对象。");
        }

        return document.RootElement.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.ToString(),
            StringComparer.Ordinal);
    }

    public static string PrettyPrint(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.Empty;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, DisplayJsonOptions);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    public static string FormatArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "[]";
        }

        string source = json.Trim();
        try
        {
            using JsonDocument document = JsonDocument.Parse(source);
            return JsonSerializer.Serialize(document.RootElement, DisplayJsonOptions);
        }
        catch (JsonException)
        {
            using JsonDocument wrapped = JsonDocument.Parse($"[{source}]");
            return JsonSerializer.Serialize(wrapped.RootElement, DisplayJsonOptions);
        }
    }

    private static object? ConvertElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(
            property => property.Name,
            property => ConvertElement(property.Value),
            StringComparer.Ordinal),
        JsonValueKind.Array => element.EnumerateArray().Select(ConvertElement).ToList(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number when element.TryGetInt32(out int number) => number,
        JsonValueKind.Number when element.TryGetInt64(out long number) => number,
        JsonValueKind.Number when element.TryGetDecimal(out decimal number) => number,
        JsonValueKind.Number => double.Parse(element.GetRawText(), CultureInfo.InvariantCulture),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => element.ToString()
    };
}
