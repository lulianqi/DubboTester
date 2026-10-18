using System.Text;
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

    public static string[] ParseJavaTypes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];

        var types = new List<string>();
        int genericDepth = 0;
        int start = 0;
        for (int index = 0; index < value.Length; index++)
        {
            switch (value[index])
            {
                case '<':
                    genericDepth++;
                    break;
                case '>':
                    genericDepth = Math.Max(0, genericDepth - 1);
                    break;
                case ',' when genericDepth == 0:
                    AddJavaType(types, value[start..index]);
                    start = index + 1;
                    break;
            }
        }

        AddJavaType(types, value[start..]);
        return [.. types];
    }

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
        // NHessian 0.4.3 has no System.Decimal encoder. Passing a decimal makes
        // it reflect Decimal's internal fields as a POJO and recurse until a
        // StackOverflowException. Hessian does support double, and Dubbo's
        // CompatibleTypeUtils converts Number values to Java BigDecimal when
        // the target DTO property is java.math.BigDecimal.
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => element.ToString()
    };

    private static void AddJavaType(List<string> types, string value)
    {
        string type = EraseGenericArguments(value);
        if (string.IsNullOrWhiteSpace(type)) return;

        types.Add(type.EndsWith("...", StringComparison.Ordinal)
            ? $"{type[..^3]}[]"
            : type);
    }

    // GenericService.$invoke expects JVM-loadable erased class names. Keep commas
    // inside <...> together while splitting, then remove those generic arguments.
    private static string EraseGenericArguments(string value)
    {
        var erased = new StringBuilder(value.Length);
        int genericDepth = 0;
        foreach (char character in value.Trim())
        {
            if (character == '<')
            {
                genericDepth++;
                continue;
            }
            if (character == '>')
            {
                genericDepth = Math.Max(0, genericDepth - 1);
                continue;
            }
            if (genericDepth == 0) erased.Append(character);
        }
        return erased.ToString().Trim();
    }
}
