using DubboTester.Models;

namespace DubboTester.Services;

/// <summary>Parses the escaped request target identically for Kestrel and HttpListener.</summary>
internal sealed record HttpGatewayRoute(
    string RegistryRootPath, string ServiceName, string MethodName,
    string? Version, string? Group, string[]? ParameterTypes)
{
    public static HttpGatewayRoute? Parse(string requestTarget)
    {
        const string prefix = "/api/dubbo/";
        string[] parts = requestTarget.Split('?', 2);
        if (!parts[0].StartsWith(prefix, StringComparison.Ordinal)) return null;
        string[] segments = parts[0][prefix.Length..].Split('/');
        // The root directory '/' is represented by no directory segments. A lone hash is not a route.
        if (segments.Length < 2 || segments.Any(string.IsNullOrEmpty)) return null;
        segments = segments.Select(segment => Decode(segment, query: false)).ToArray();
        if (segments.Any(segment => segment is "." or ".." || segment.Any(c => c is '/' or '\\' || char.IsControl(c))))
            return null;

        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        if (parts.Length == 2)
        {
            foreach (string item in parts[1].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] pair = item.Split('=', 2);
                string key = Decode(pair[0], query: true);
                if (key is not ("version" or "group" or "parameterTypes"))
                    throw new ArgumentException("路由查询参数仅支持 version、group 和 parameterTypes。");
                if (!query.TryAdd(key, Decode(pair.Length == 2 ? pair[1] : "", query: true)))
                    throw new ArgumentException($"路由参数 {key} 不能重复填写。");
            }
        }

        string[]? types = null;
        if (query.TryGetValue("parameterTypes", out string? parameterTypes))
        {
            types = parameterTypes.Length == 0 ? [] : parameterTypes.Split(',', StringSplitOptions.TrimEntries);
            if (types.Any(string.IsNullOrEmpty))
                throw new ArgumentException("parameterTypes 使用逗号分隔 Java 参数类型；无参数方法使用 parameterTypes=。");
        }
        return new("/" + string.Join('/', segments[..^2]), segments[^2], segments[^1],
            query.GetValueOrDefault("version"), query.GetValueOrDefault("group"), types);
    }

    public bool Matches(HttpGatewayMethod method) =>
        RegistryRootPath == RegistryRootPaths.NormalizeRoot(method.RegistryRootPath)
        && ServiceName == method.ServiceName && MethodName == method.MethodName
        && (Version is null || Version == method.Version)
        && (Group is null || Group == method.Group)
        && (ParameterTypes is null || ParameterTypes.SequenceEqual(method.ParameterTypes, StringComparer.Ordinal));

    private static string Decode(string value, bool query)
    {
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '%') continue;
            if (i + 2 >= value.Length || !Uri.IsHexDigit(value[i + 1]) || !Uri.IsHexDigit(value[i + 2]))
                throw new ArgumentException("请求地址包含无效的 URL 编码。");
            i += 2;
        }
        return Uri.UnescapeDataString(query ? value.Replace('+', ' ') : value);
    }
}
