using System.Text.Json.Serialization;
using DubboTester.Services;

namespace DubboTester.Models;

public enum McpToolValidationStatus
{
    Available = 0,
    PendingValidation = 1,
    ServiceMissing = 2,
    MethodMissing = 3,
    SignatureMismatch = 4,
    ValidationFailed = 5,
    MetadataWarning = 6
}

public sealed class McpGatewayConfiguration
{
    public bool Enabled { get; set; }
    public RegistryCenterType RegistryType { get; set; } = RegistryCenterType.ZooKeeper;
    public string ZooKeeperAddress { get; set; } = string.Empty;

    [JsonIgnore]
    public string RegistryAddress
    {
        get => ZooKeeperAddress;
        set => ZooKeeperAddress = value;
    }

    [JsonIgnore]
    public string RegistryConnectionString =>
        RegistryConnectionSettings.BuildConnectionString(RegistryType, RegistryAddress);

    [JsonIgnore]
    public string RegistryDisplayName => RegistryConnectionSettings.DisplayName(RegistryType);
    public string RegistryRootPath { get; set; } = "/";
    public string MetadataCenterAddress { get; set; } = string.Empty;
    public string MetadataRootPath { get; set; } = string.Empty;
    public int RequestTimeoutMs { get; set; } = 60_000;
    public int HistoryLimit { get; set; } = 5_000;
    public bool AuthenticationEnabled { get; set; }
    public bool OnlyRefreshServiceNames { get; set; }
    public string AccessToken { get; set; } = string.Empty;
    public List<McpDubboMethodConfiguration> Methods { get; set; } = [];

    public McpGatewayConfiguration Clone() => new()
    {
        Enabled = Enabled,
        RegistryType = RegistryType,
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
        RegistryType = RegistryType,
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
    public string RegistryRootPath { get; set; } = "/";
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
    public McpToolValidationStatus ValidationStatus { get; set; } = McpToolValidationStatus.Available;
    public string ValidationMessage { get; set; } = string.Empty;
    public DateTimeOffset? ValidatedAt { get; set; }

    [JsonIgnore]
    public bool IsValidationUsable => ValidationStatus is
        McpToolValidationStatus.Available or McpToolValidationStatus.MetadataWarning;

    public bool IsAvailableForMcp => Enabled && IsValidationUsable;

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
        ExampleRequestJson = ExampleRequestJson,
        ValidationStatus = ValidationStatus,
        ValidationMessage = ValidationMessage,
        ValidatedAt = ValidatedAt
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

public sealed record McpToolValidationSummary(
    int TotalCount,
    int AvailableCount,
    int UnavailableCount)
{
    public int WarningCount { get; init; }
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

public sealed record McpCallRecordPage(
    IReadOnlyList<McpCallRecord> Items,
    int TotalCount,
    int OverallCount);
