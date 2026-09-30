using System.Globalization;
using System.Text;
using System.Text.Json;
using DubboTester.Models;

namespace DubboTester.Services;

public readonly record struct BusinessAssertionEvaluation(bool Success, string? Error);

public static class PressureTestBusinessAssertionEvaluator
{
    public static void Validate(BusinessAssertionConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        for (int index = 0; index < configuration.Conditions.Count; index++)
        {
            BusinessAssertionCondition condition = configuration.Conditions[index];
            if (condition.Type == BusinessAssertionType.ResponseContains)
            {
                if (string.IsNullOrEmpty(condition.ExpectedValue))
                    throw new ArgumentException($"业务断言第 {index + 1} 条的包含内容不能为空。");
                continue;
            }

            if (string.IsNullOrWhiteSpace(condition.JsonPath)
                || !condition.JsonPath.TrimStart().StartsWith('$'))
            {
                throw new ArgumentException($"业务断言第 {index + 1} 条需要填写以 $ 开头的 JSONPath。");
            }

            if (RequiresExpectedValue(condition.Operator)
                && string.IsNullOrEmpty(condition.ExpectedValue))
            {
                throw new ArgumentException($"业务断言第 {index + 1} 条需要填写期望值。");
            }
        }
    }

    public static BusinessAssertionEvaluation Evaluate(
        BusinessAssertionConfiguration configuration,
        string response)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Conditions.Count == 0)
        {
            return new BusinessAssertionEvaluation(true, null);
        }

        var evaluations = new List<(bool Success, string Description)>(configuration.Conditions.Count);
        JsonDocument? document = null;
        string? jsonError = null;
        if (configuration.Conditions.Any(condition => condition.Type == BusinessAssertionType.JsonPath))
        {
            try { document = JsonDocument.Parse(response); }
            catch (JsonException exception) { jsonError = exception.Message; }
        }

        using (document)
        {
            foreach (BusinessAssertionCondition condition in configuration.Conditions)
            {
                evaluations.Add(condition.Type switch
                {
                    BusinessAssertionType.ResponseContains => EvaluateContains(condition, response),
                    BusinessAssertionType.JsonPath => EvaluateJsonPath(condition, document, jsonError),
                    _ => (false, "不支持的业务断言类型")
                });
            }
        }

        bool success = configuration.Join == BusinessAssertionJoin.And
            ? evaluations.All(item => item.Success)
            : evaluations.Any(item => item.Success);
        if (success)
        {
            return new BusinessAssertionEvaluation(true, null);
        }

        string joinText = configuration.Join == BusinessAssertionJoin.And ? "AND" : "OR";
        string details = string.Join("；", evaluations
            .Where(item => !item.Success)
            .Select(item => item.Description));
        return new BusinessAssertionEvaluation(
            false,
            $"业务断言失败（{joinText}）：{details}");
    }

    public static string Describe(BusinessAssertionConfiguration configuration)
    {
        if (configuration.Conditions.Count == 0) return "未配置";
        string join = configuration.Join == BusinessAssertionJoin.And ? " AND " : " OR ";
        return string.Join(join, configuration.Conditions.Select(Describe));
    }

    public static bool RequiresExpectedValue(BusinessAssertionOperator value) =>
        value is not BusinessAssertionOperator.Exists
            and not BusinessAssertionOperator.NotExists;

    private static (bool Success, string Description) EvaluateContains(
        BusinessAssertionCondition condition,
        string response)
    {
        bool success = response.Contains(condition.ExpectedValue, StringComparison.Ordinal);
        return (success, $"响应应包含“{condition.ExpectedValue}”");
    }

    private static (bool Success, string Description) EvaluateJsonPath(
        BusinessAssertionCondition condition,
        JsonDocument? document,
        string? jsonError)
    {
        string path = condition.JsonPath.Trim();
        if (document is null)
        {
            return (false, $"{path} 无法读取：响应不是合法 JSON（{jsonError ?? "解析失败"}）");
        }

        bool found = TryResolve(document.RootElement, path, out JsonElement actual);
        bool success = condition.Operator switch
        {
            BusinessAssertionOperator.Exists => found,
            BusinessAssertionOperator.NotExists => !found,
            _ when !found => false,
            BusinessAssertionOperator.Equals => ValuesEqual(actual, condition.ExpectedValue),
            BusinessAssertionOperator.NotEquals => !ValuesEqual(actual, condition.ExpectedValue),
            BusinessAssertionOperator.Contains => ScalarText(actual)
                .Contains(condition.ExpectedValue, StringComparison.Ordinal),
            BusinessAssertionOperator.NotContains => !ScalarText(actual)
                .Contains(condition.ExpectedValue, StringComparison.Ordinal),
            BusinessAssertionOperator.GreaterThan => CompareNumber(actual, condition.ExpectedValue, value => value > 0),
            BusinessAssertionOperator.GreaterThanOrEqual => CompareNumber(actual, condition.ExpectedValue, value => value >= 0),
            BusinessAssertionOperator.LessThan => CompareNumber(actual, condition.ExpectedValue, value => value < 0),
            BusinessAssertionOperator.LessThanOrEqual => CompareNumber(actual, condition.ExpectedValue, value => value <= 0),
            _ => false
        };
        return (success, Describe(condition) + (found ? $"，实际为 {ScalarText(actual)}" : "，路径不存在"));
    }

    private static string Describe(BusinessAssertionCondition condition)
    {
        if (condition.Type == BusinessAssertionType.ResponseContains)
            return $"响应包含“{condition.ExpectedValue}”";

        string operation = condition.Operator switch
        {
            BusinessAssertionOperator.Exists => "存在",
            BusinessAssertionOperator.NotExists => "不存在",
            BusinessAssertionOperator.Equals => "等于",
            BusinessAssertionOperator.NotEquals => "不等于",
            BusinessAssertionOperator.Contains => "包含",
            BusinessAssertionOperator.NotContains => "不包含",
            BusinessAssertionOperator.GreaterThan => "大于",
            BusinessAssertionOperator.GreaterThanOrEqual => "大于等于",
            BusinessAssertionOperator.LessThan => "小于",
            BusinessAssertionOperator.LessThanOrEqual => "小于等于",
            _ => condition.Operator.ToString()
        };
        return RequiresExpectedValue(condition.Operator)
            ? $"{condition.JsonPath} {operation} {condition.ExpectedValue}"
            : $"{condition.JsonPath} {operation}";
    }

    private static bool TryResolve(JsonElement root, string path, out JsonElement result)
    {
        result = root;
        int index = 0;
        SkipWhitespace(path, ref index);
        if (index >= path.Length || path[index] != '$') return false;
        index++;

        while (true)
        {
            SkipWhitespace(path, ref index);
            if (index >= path.Length) return true;

            if (path[index] == '.')
            {
                index++;
                int start = index;
                while (index < path.Length && path[index] is not '.' and not '[' && !char.IsWhiteSpace(path[index]))
                    index++;
                if (index == start || result.ValueKind != JsonValueKind.Object) return false;
                if (!result.TryGetProperty(path[start..index], out result)) return false;
                continue;
            }

            if (path[index] != '[') return false;
            index++;
            SkipWhitespace(path, ref index);
            if (index >= path.Length) return false;

            if (path[index] is '\'' or '"')
            {
                char quote = path[index++];
                var property = new StringBuilder();
                bool closed = false;
                while (index < path.Length)
                {
                    char character = path[index++];
                    if (character == quote) { closed = true; break; }
                    if (character == '\\' && index < path.Length) character = path[index++];
                    property.Append(character);
                }
                SkipWhitespace(path, ref index);
                if (!closed || index >= path.Length || path[index++] != ']'
                    || result.ValueKind != JsonValueKind.Object
                    || !result.TryGetProperty(property.ToString(), out result)) return false;
                continue;
            }

            int numberStart = index;
            while (index < path.Length && char.IsDigit(path[index])) index++;
            if (numberStart == index
                || !int.TryParse(path[numberStart..index], NumberStyles.None, CultureInfo.InvariantCulture, out int arrayIndex))
                return false;
            SkipWhitespace(path, ref index);
            if (index >= path.Length || path[index++] != ']'
                || result.ValueKind != JsonValueKind.Array
                || arrayIndex < 0
                || arrayIndex >= result.GetArrayLength()) return false;
            result = result[arrayIndex];
        }
    }

    private static void SkipWhitespace(string value, ref int index)
    {
        while (index < value.Length && char.IsWhiteSpace(value[index])) index++;
    }

    private static bool ValuesEqual(JsonElement actual, string expected)
    {
        string trimmed = expected.Trim();
        return actual.ValueKind switch
        {
            JsonValueKind.String => string.Equals(actual.GetString(), expected, StringComparison.Ordinal),
            JsonValueKind.Number => decimal.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number)
                && actual.TryGetDecimal(out decimal actualNumber)
                && actualNumber == number,
            JsonValueKind.True => bool.TryParse(trimmed, out bool boolean) && boolean,
            JsonValueKind.False => bool.TryParse(trimmed, out bool boolean) && !boolean,
            JsonValueKind.Null => string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase),
            _ => string.Equals(actual.GetRawText(), trimmed, StringComparison.Ordinal)
        };
    }

    private static bool CompareNumber(
        JsonElement actual,
        string expected,
        Func<int, bool> predicate) =>
        actual.ValueKind == JsonValueKind.Number
        && actual.TryGetDecimal(out decimal actualNumber)
        && decimal.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal expectedNumber)
        && predicate(actualNumber.CompareTo(expectedNumber));

    private static string ScalarText(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? string.Empty
        : value.GetRawText();
}
