using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace DubboTester.Models;

public sealed class HttpGatewayConfiguration
{
    public bool Enabled { get; set; }
    public TesterSettings Settings { get; set; } = new() { RequestTimeoutMs = 60_000 };
    public bool EnableAllMethods { get; set; }
    public HashSet<string> EnabledMethodIds { get; set; } = new(StringComparer.Ordinal);
    public bool AuthenticationEnabled { get; set; } = true;
    public string AccessToken { get; set; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    public int HistoryLimit { get; set; } = 2_000;

    public HttpGatewayConfiguration Clone() => new()
    {
        Enabled = Enabled, Settings = Settings.Clone(), EnableAllMethods = EnableAllMethods,
        EnabledMethodIds = new(EnabledMethodIds, StringComparer.Ordinal),
        AuthenticationEnabled = AuthenticationEnabled, AccessToken = AccessToken, HistoryLimit = HistoryLimit
    };
}

public sealed record HttpGatewayMethod(
    string Id, string RegistryRootPath, string ServiceName, string MethodName,
    string[] ParameterTypes, string Version, string Group, string Signature,
    string ExampleArgumentsJson, string? Error = null)
{
    public bool Available => Error is null;
    public string Path
    {
        get
        {
            string root = RegistryRootPath.Trim('/');
            string[] directories = root.Length == 0 ? [] : root.Split('/');
            return "/api/dubbo/" + string.Join('/', directories.Append(ServiceName).Append(MethodName).Select(Uri.EscapeDataString));
        }
    }

    // Empty values explicitly select the unversioned/ungrouped/no-argument variant.
    public string QualifiedPath => $"{Path}?version={Uri.EscapeDataString(Version)}&group={Uri.EscapeDataString(Group)}&parameterTypes={Uri.EscapeDataString(string.Join(',', ParameterTypes))}";

    public string GetInvocationPath(IEnumerable<HttpGatewayMethod> methods)
    {
        string path = Path;
        return methods.Any(method => method.Available && method.Id != Id && method.Path == path) ? QualifiedPath : path;
    }
}

public sealed record HttpGatewayResponse(
    [property: JsonIgnore] int StatusCode,
    bool Success, object? Data, string? Error, long ElapsedMs = 0);

public sealed record HttpCallRecord(
    DateTimeOffset CreatedAt, string RegistryRootPath, string ServiceName, string MethodName,
    string Path, int StatusCode, long ElapsedMs, string RemoteAddress, string Request, string Response)
{
    public string Method => string.IsNullOrWhiteSpace(ServiceName)
        ? MethodName
        : $"{ServiceName}#{MethodName}";
}

public sealed record HttpCallRecordPage(IReadOnlyList<HttpCallRecord> Records, int MatchCount, int TotalCount);
