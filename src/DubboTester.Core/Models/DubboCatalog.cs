using DubboNet.Clients.DataModle;

namespace DubboTester.Models;

public sealed class DubboServiceItem
{
    public required string Name { get; init; }
    public string RegistryRootPath { get; init; } = string.Empty;
    public List<DubboProviderItem> Providers { get; } = [];
    public List<string> Methods { get; } = [];
    public bool IsLoaded { get; set; }
}

public sealed class DubboProviderItem
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string Scheme { get; init; }
    public required string ServiceName { get; init; }
    public string? Application { get; init; }
    public string? Version { get; init; }
    public string? Group { get; init; }
    public string? Release { get; init; }
    public int Weight { get; init; } = 100;
    public bool Disabled { get; init; }
    public string[] Methods { get; init; } = [];
    public required string RegistryUrl { get; init; }

    public string Address => $"{Host}:{Port}";
    public string Description => string.IsNullOrWhiteSpace(Application)
        ? $"{Scheme}://{Address}"
        : $"{Scheme}://{Address} · {Application}";
}

public sealed record DubboCatalogLoadResult(
    IReadOnlyList<DubboServiceItem> Services,
    bool FromCache,
    bool HasAllServiceDetails,
    bool PreloadAttempted)
{
    public int LoadedServiceDetailCount => Services.Count(service => service.IsLoaded);
    public int PendingServiceDetailCount => Services.Count - LoadedServiceDetailCount;
    public IReadOnlyList<string> RegistryRoots => Services
        .Select(service => service.RegistryRootPath)
        .Where(root => !string.IsNullOrWhiteSpace(root))
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();
}

public enum CatalogPreloadState
{
    Running,
    Completed,
    Stopped,
    TimedOut,
    ConsecutiveFailures,
    Faulted
}

public sealed record CatalogPreloadStatus(
    string ZooKeeperAddress,
    string RegistryRoot,
    CatalogPreloadState State,
    int TotalServiceCount,
    int LoadedServiceCount,
    int FailedServiceCount,
    DateTimeOffset StartedAt,
    string? Error = null)
{
    public int PendingServiceCount =>
        Math.Max(0, TotalServiceCount - LoadedServiceCount - FailedServiceCount);
}

public sealed class MethodSignatureItem
{
    public required string ServiceName { get; init; }
    public required string MethodName { get; init; }
    public required string[] ParameterTypes { get; init; }
    public string? ReturnType { get; init; }
    public string? Source { get; init; }
    public string? Version { get; init; }
    public string? Group { get; init; }
    public string? Application { get; init; }
    public string? MetadataPath { get; init; }
    public string? CodeSource { get; init; }
    public IReadOnlyDictionary<string, string> ServiceParameters { get; init; } =
        new Dictionary<string, string>();
    public IReadOnlyList<DubboTypeMetadata> TypeDefinitions { get; init; } = [];
    public int MetadataVersion { get; init; }
    public DateTimeOffset LoadedAt { get; init; }

    public string Signature =>
        $"{ReturnType ?? "?"} {MethodName}({string.Join(", ", ParameterTypes)})";

    public static MethodSignatureItem FromMetadata(
        DubboMethodMetadata metadata,
        IReadOnlyList<DubboTypeMetadata>? serviceTypeDefinitions = null) => new()
    {
        ServiceName = metadata.ServiceName,
        MethodName = metadata.Name,
        ParameterTypes = metadata.ParameterTypes ?? [],
        ReturnType = metadata.ReturnType,
        Source = metadata.MetadataSource.ToString(),
        Version = metadata.Version,
        Group = metadata.Group,
        Application = metadata.Application,
        MetadataPath = metadata.MetadataPath,
        CodeSource = metadata.CodeSource,
        ServiceParameters = metadata.ServiceParameters ?? new Dictionary<string, string>(),
        TypeDefinitions = serviceTypeDefinitions is { Count: > 0 }
            ? serviceTypeDefinitions
            : metadata.TypeDefinitions ?? [],
        MetadataVersion = metadata.MetadataVersion,
        LoadedAt = metadata.LoadedAt
    };
}
