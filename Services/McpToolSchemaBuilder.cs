using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DubboTester.Models;

namespace DubboTester.Services;

public static class McpToolSchemaBuilder
{
    public static string BuildToolName(string serviceName, string methodName, IEnumerable<string> parameterTypes)
    {
        string service = serviceName.Split('.').LastOrDefault() ?? "service";
        string baseName = Sanitize($"dubbo_{service}_{methodName}").ToLowerInvariant();
        string signature = $"{serviceName}#{methodName}({string.Join(",", parameterTypes)})";
        string suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature)))[..8].ToLowerInvariant();
        int maximumBaseLength = Math.Max(1, 63 - suffix.Length - 1);
        if (baseName.Length > maximumBaseLength) baseName = baseName[..maximumBaseLength].TrimEnd('_');
        return $"{baseName}_{suffix}";
    }

    public static string EnsureUniqueToolName(string preferredName, IEnumerable<string> existingToolNames)
    {
        var existing = new HashSet<string>(existingToolNames, StringComparer.Ordinal);
        if (!existing.Contains(preferredName)) return preferredName;

        for (int index = 2; ; index++)
        {
            string suffix = $"_{index}";
            int maximumBaseLength = Math.Max(1, 64 - suffix.Length);
            string baseName = preferredName.Length > maximumBaseLength
                ? preferredName[..maximumBaseLength].TrimEnd('_', '-')
                : preferredName;
            string candidate = baseName + suffix;
            if (!existing.Contains(candidate)) return candidate;
        }
    }

    public static string BuildInputSchema(
        IReadOnlyList<MethodParameterStructure> parameters,
        string parameterGuidance)
    {
        var argumentSchema = new JsonObject
        {
            ["type"] = "array",
            ["description"] = string.IsNullOrWhiteSpace(parameterGuidance)
                ? "按 Java 方法签名顺序传入参数。"
                : parameterGuidance.Trim(),
            ["minItems"] = parameters.Count,
            ["maxItems"] = parameters.Count,
            ["prefixItems"] = new JsonArray(parameters
                .Select(parameter => BuildNodeSchema(parameter.Structure))
                .ToArray())
        };

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["arguments"] = argumentSchema,
                ["attachments"] = new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "可选的 Dubbo attachments，值会转换为字符串。",
                    ["additionalProperties"] = true
                }
            }
        };
        if (parameters.Count > 0)
        {
            schema["required"] = new JsonArray("arguments");
        }

        return schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static string BuildDescription(McpDubboMethodConfiguration method)
    {
        var parts = new List<string> { method.Description.Trim() };
        if (!string.IsNullOrWhiteSpace(method.TriggerCondition))
            parts.Add($"适用/触发条件：{method.TriggerCondition.Trim()}");
        if (!string.IsNullOrWhiteSpace(method.ParameterGuidance))
            parts.Add($"参数要求：{method.ParameterGuidance.Trim()}");
        parts.Add($"Dubbo 方法：{method.ServiceName}#{method.Signature}");
        return string.Join("\n", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static JsonObject BuildNodeSchema(JavaTypeStructure node)
    {
        var schema = new JsonObject { ["description"] = $"Java 类型：{node.JavaType}" };
        switch (node.Kind)
        {
            case JavaTypeKind.Object:
                schema["type"] = "object";
                schema["properties"] = new JsonObject(node.Children
                    .Select(child => KeyValuePair.Create<string, JsonNode?>(child.Name, BuildNodeSchema(child))));
                schema["additionalProperties"] = false;
                break;
            case JavaTypeKind.Collection:
                schema["type"] = "array";
                schema["items"] = node.Children.Count > 0
                    ? BuildNodeSchema(node.Children[0])
                    : new JsonObject();
                break;
            case JavaTypeKind.Map:
                schema["type"] = "object";
                schema["additionalProperties"] = node.Children.Count > 0
                    ? BuildNodeSchema(node.Children[0])
                    : true;
                break;
            case JavaTypeKind.Enum:
                schema["type"] = "string";
                if (node.EnumValues.Count > 0)
                    schema["enum"] = new JsonArray(
                        node.EnumValues.Select(value => JsonValue.Create(value)).ToArray());
                break;
            case JavaTypeKind.RecursiveReference:
                schema["type"] = new JsonArray("object", "null");
                break;
            case JavaTypeKind.Unknown:
                break;
            default:
                ApplyScalarType(schema, node.JavaType);
                break;
        }
        return schema;
    }

    private static void ApplyScalarType(JsonObject schema, string javaType)
    {
        string type = javaType.Trim();
        if (type is "boolean" or "java.lang.Boolean")
        {
            schema["type"] = "boolean";
        }
        else if (type is "byte" or "short" or "int" or "long"
            or "java.lang.Byte" or "java.lang.Short" or "java.lang.Integer" or "java.lang.Long"
            or "java.math.BigInteger")
        {
            schema["type"] = "integer";
        }
        else if (type is "float" or "double" or "java.lang.Float" or "java.lang.Double"
            or "java.math.BigDecimal")
        {
            schema["type"] = "number";
        }
        else
        {
            schema["type"] = "string";
            if (type is "java.util.Date" or "java.sql.Timestamp" or "java.time.LocalDateTime")
                schema["pattern"] = "^\\d{4}-\\d{2}-\\d{2} \\d{2}:\\d{2}:\\d{2}$";
            else if (type is "java.sql.Date" or "java.time.LocalDate")
                schema["format"] = "date";
            else if (type.StartsWith("java.time.", StringComparison.Ordinal) || type == "java.time.Instant")
                schema["format"] = "date-time";
        }
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        bool lastUnderscore = false;
        foreach (char character in value)
        {
            bool valid = char.IsAsciiLetterOrDigit(character) || character is '_' or '-';
            char output = valid ? character : '_';
            if (output == '_' && lastUnderscore) continue;
            builder.Append(output);
            lastUnderscore = output == '_';
        }
        return builder.ToString().Trim('_');
    }
}
