namespace DubboTester.Models;

public sealed class McpGatewayConfiguration
{
    public bool Enabled { get; set; }
    public string ZooKeeperAddress { get; set; } = string.Empty;
    public string RegistryRootPath { get; set; } = "/dubbo";
    public string MetadataCenterAddress { get; set; } = string.Empty;
    public string MetadataRootPath { get; set; } = string.Empty;
    public int RequestTimeoutMs { get; set; } = 60_000;
    public int HistoryLimit { get; set; } = 200;
    public bool AuthenticationEnabled { get; set; } = true;
    public bool OnlyRefreshServiceNames { get; set; }
    public string AccessToken { get; set; } = string.Empty;
    public List<McpDubboMethodConfiguration> Methods { get; set; } = [];

    public McpGatewayConfiguration Clone() => new()
    {
        Enabled = Enabled,
        ZooKeeperAddress = ZooKeeperAddress,
        RegistryRootPath = RegistryRootPath,
        MetadataCenterAddress = MetadataCenterAddress,
        MetadataRootPath = MetadataRootPath,
        RequestTimeoutMs = RequestTimeoutMs,
        HistoryLimit = HistoryLimit,
        AuthenticationEnabled = AuthenticationEnabled,
        OnlyRefreshServiceNames = OnlyRefreshServiceNames,
        AccessToken = AccessToken,
        Methods = Methods.Select(method => method.Clone()).ToList()
    };

    public TesterSettings ToTesterSettings() => new()
    {
        ZooKeeperAddress = ZooKeeperAddress,
        RegistryRootPath = RegistryRootPath,
        MetadataCenterAddress = MetadataCenterAddress,
        MetadataRootPath = MetadataRootPath,
        RequestTimeoutMs = RequestTimeoutMs
    };
}

public sealed class McpDubboMethodConfiguration
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; }
    public string ToolName { get; set; } = string.Empty;
    public string RegistryRootPath { get; set; } = "/dubbo";
    public string ServiceName { get; set; } = string.Empty;
    public string MethodName { get; set; } = string.Empty;
    public string[] ParameterTypes { get; set; } = [];
    public string ReturnType { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public bool ParameterTypesManuallyConfigured { get; set; }
    public string Description { get; set; } = string.Empty;
    public string TriggerCondition { get; set; } = string.Empty;
    public string ParameterGuidance { get; set; } = string.Empty;
    public string InputSchemaJson { get; set; } = "{\"type\":\"object\"}";
    public string ExampleRequestJson { get; set; } = "[]";

    public string ConfigurationKey => BuildConfigurationKey(
        RegistryRootPath,
        ServiceName,
        MethodName,
        ParameterTypes,
        Version,
        Group);

    public string Signature =>
        $"{(string.IsNullOrWhiteSpace(ReturnType) ? "?" : ReturnType)} {MethodName}({string.Join(", ", ParameterTypes)})";

    public McpDubboMethodConfiguration Clone() => new()
    {
        Id = Id,
        Enabled = Enabled,
        ToolName = ToolName,
        RegistryRootPath = RegistryRootPath,
        ServiceName = ServiceName,
        MethodName = MethodName,
        ParameterTypes = [.. ParameterTypes],
        ReturnType = ReturnType,
        Version = Version,
        Group = Group,
        ParameterTypesManuallyConfigured = ParameterTypesManuallyConfigured,
        Description = Description,
        TriggerCondition = TriggerCondition,
        ParameterGuidance = ParameterGuidance,
        InputSchemaJson = InputSchemaJson,
        ExampleRequestJson = ExampleRequestJson
    };

    public static string BuildConfigurationKey(
        string? registryRootPath,
        string? serviceName,
        string? methodName,
        IEnumerable<string>? parameterTypes,
        string? version,
        string? group) => string.Join(
            "|",
            registryRootPath?.Trim() ?? string.Empty,
            serviceName?.Trim() ?? string.Empty,
            methodName?.Trim() ?? string.Empty,
            string.Join(",", parameterTypes ?? []),
            version?.Trim() ?? string.Empty,
            group?.Trim() ?? string.Empty);
}

public sealed class McpCallRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public string ToolName { get; init; } = string.Empty;
    public string RegistryRootPath { get; init; } = string.Empty;
    public string ServiceName { get; init; } = string.Empty;
    public string MethodName { get; init; } = string.Empty;
    public string RemoteAddress { get; init; } = string.Empty;
    public string RequestJson { get; init; } = string.Empty;
    public string Response { get; init; } = string.Empty;
    public bool Success { get; init; }
    public long ElapsedMs { get; init; }
    public string Error { get; init; } = string.Empty;
}
