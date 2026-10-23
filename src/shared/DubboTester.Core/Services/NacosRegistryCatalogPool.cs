using Microsoft.Extensions.DependencyInjection;
using Nacos.V2;
using Nacos.V2.DependencyInjection;
using Nacos.V2.Naming.Dtos;
using Nacos.V2.Remote;
using DubboTester.Models;
using System.Net;

namespace DubboTester.Services;

/// <summary>
/// Reuses the Nacos naming clients used to build the tester's service tree. DubboNet
/// owns its invocation client, while this pool only supplies catalog enumeration that
/// is not exposed by DubboNet's public API.
/// </summary>
internal sealed class NacosRegistryCatalogPool
{
    private const int MaximumClients = 32;
    private readonly object _sync = new();
    private readonly Dictionary<string, NacosRegistryCatalogClient> _clients =
        new(StringComparer.Ordinal);

    public static NacosRegistryCatalogPool Shared { get; } = new();

    public NacosRegistryCatalogClient Get(string connectionString)
    {
        string key = NormalizeConnectionString(connectionString);
        lock (_sync)
        {
            if (_clients.TryGetValue(key, out NacosRegistryCatalogClient? existing))
            {
                return existing;
            }

            if (_clients.Count >= MaximumClients)
            {
                throw new InvalidOperationException(
                    $"当前进程已连接 {MaximumClients} 个 Nacos 配置，请重启应用后再切换注册中心。");
            }

            var created = new NacosRegistryCatalogClient(key);
            _clients[key] = created;
            return created;
        }
    }

    internal static string NormalizeConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)
            || !connectionString.TrimStart().StartsWith("nacos://", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Nacos 连接地址必须以 nacos:// 开头。", nameof(connectionString));
        }

        return connectionString.Trim();
    }
}

internal sealed class NacosRegistryCatalogClient
{
    private const int PageSize = 500;
    private readonly IServiceProvider _serviceProvider;
    private readonly INacosNamingService _namingService;

    public NacosRegistryCatalogClient(string connectionString)
    {
        NacosConnectionOptions parsed = ParseConnectionString(connectionString);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNacosV2Naming(options =>
        {
            options.ServerAddresses = parsed.ServerAddresses;
            options.Namespace = parsed.Namespace;
            options.UserName = parsed.UserName;
            options.Password = parsed.Password;
            options.AccessKey = parsed.AccessKey;
            options.SecretKey = parsed.SecretKey;
            options.NamingUseRpc = true;
            options.ConfigUseRpc = true;
        });
        _serviceProvider = services.BuildServiceProvider();
        _namingService = _serviceProvider.GetRequiredService<INacosNamingService>();
    }

    public async Task<IReadOnlyList<string>> GetServiceNamesAsync(
        string group,
        CancellationToken cancellationToken = default)
    {
        string effectiveGroup = NormalizeGroup(group);
        var interfaces = new HashSet<string>(StringComparer.Ordinal);
        int pageNumber = 1;
        while (true)
        {
            ListView<string>? page = await _namingService
                .GetServicesOfServer(pageNumber, PageSize, effectiveGroup)
                .WaitAsync(cancellationToken);
            if (page?.Data is not { Count: > 0 }) break;

            foreach (string serviceName in page.Data)
            {
                string? interfaceName = ExtractInterfaceName(serviceName);
                if (!string.IsNullOrWhiteSpace(interfaceName)) interfaces.Add(interfaceName);
            }

            if (pageNumber * PageSize >= page.Count) break;
            pageNumber++;
        }

        return interfaces.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<IReadOnlyList<DubboProviderItem>> GetProvidersAsync(
        string interfaceName,
        string group,
        CancellationToken cancellationToken = default)
    {
        string effectiveGroup = NormalizeGroup(group);
        IReadOnlyList<string> serviceNames = await GetRawServiceNamesAsync(
            effectiveGroup,
            cancellationToken);
        var providers = new List<DubboProviderItem>();
        foreach (string nacosServiceName in serviceNames.Where(name =>
                     IsProviderServiceName(name, interfaceName)))
        {
            List<Instance>? instances = await _namingService
                .GetAllInstances(nacosServiceName, effectiveGroup, false)
                .WaitAsync(cancellationToken);
            foreach (Instance instance in instances ?? [])
            {
                DubboProviderItem? provider = ConvertInstance(instance, interfaceName);
                if (provider is not null) providers.Add(provider);
            }
        }

        return providers
            .GroupBy(item => $"{item.Scheme}://{item.Address}", StringComparer.OrdinalIgnoreCase)
            .Select(grouping => grouping.First())
            .OrderBy(item => item.Address, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task TestAsync(
        string group,
        CancellationToken cancellationToken = default)
    {
        _ = await _namingService
            .GetServicesOfServer(1, 1, NormalizeGroup(group))
            .WaitAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<string>> GetRawServiceNamesAsync(
        string group,
        CancellationToken cancellationToken)
    {
        var services = new List<string>();
        int pageNumber = 1;
        while (true)
        {
            ListView<string>? page = await _namingService
                .GetServicesOfServer(pageNumber, PageSize, group)
                .WaitAsync(cancellationToken);
            if (page?.Data is not { Count: > 0 }) break;
            services.AddRange(page.Data.Where(name => !string.IsNullOrWhiteSpace(name)));
            if (pageNumber * PageSize >= page.Count) break;
            pageNumber++;
        }

        return services.Distinct(StringComparer.Ordinal).ToArray();
    }

    internal static string? ExtractInterfaceName(string? nacosServiceName)
    {
        if (string.IsNullOrWhiteSpace(nacosServiceName)) return null;
        const string prefix = "providers:";
        if (nacosServiceName.StartsWith(prefix, StringComparison.Ordinal))
        {
            string remainder = nacosServiceName[prefix.Length..];
            int separator = remainder.IndexOf(':');
            return separator < 0 ? remainder : remainder[..separator];
        }

        // DubboNet also accepts providers registered directly under the interface
        // name. Restrict these to Java-style names to avoid unrelated Nacos services.
        return nacosServiceName.Contains('.', StringComparison.Ordinal)
               && !nacosServiceName.Contains(':', StringComparison.Ordinal)
            ? nacosServiceName
            : null;
    }

    private static bool IsProviderServiceName(string name, string interfaceName)
    {
        string prefix = "providers:" + interfaceName;
        return string.Equals(name, prefix, StringComparison.Ordinal)
            || name.StartsWith(prefix + ":", StringComparison.Ordinal)
            || string.Equals(name, interfaceName, StringComparison.Ordinal);
    }

    internal static DubboProviderItem? ConvertInstance(
        Instance instance,
        string fallbackInterfaceName)
    {
        if (instance is null || !IPAddress.TryParse(instance.Ip, out _)
            || instance.Port is < 1 or > 65_535)
        {
            return null;
        }

        IReadOnlyDictionary<string, string> metadata = instance.Metadata
            ?? new Dictionary<string, string>();
        string scheme = GetValue(metadata, "protocol") ?? "dubbo";
        if (!string.Equals(scheme, "dubbo", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(scheme, "tri", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string serviceName = GetValue(metadata, "interface") ?? fallbackInterfaceName;
        bool metadataDisabled = bool.TryParse(
            GetValue(metadata, "disabled"),
            out bool parsedDisabled) && parsedDisabled;
        int weight = int.TryParse(GetValue(metadata, "weight"), out int metadataWeight)
            ? metadataWeight
            : Math.Max(0, (int)Math.Round(instance.Weight * 100D));
        string[] methods = (GetValue(metadata, "methods") ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var parameters = new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["interface"] = serviceName,
            ["disabled"] = (!instance.Healthy || !instance.Enabled || metadataDisabled)
                .ToString().ToLowerInvariant(),
            ["weight"] = weight.ToString()
        };
        string query = string.Join("&", parameters.Select(item =>
            Uri.EscapeDataString(item.Key) + "=" + Uri.EscapeDataString(item.Value ?? string.Empty)));
        var uri = new UriBuilder(scheme, instance.Ip, instance.Port, serviceName)
        {
            Query = query
        }.Uri;

        return new DubboProviderItem
        {
            Host = instance.Ip,
            Port = instance.Port,
            Scheme = scheme.ToLowerInvariant(),
            ServiceName = serviceName,
            Application = GetValue(metadata, "application"),
            Version = GetValue(metadata, "version"),
            Group = GetValue(metadata, "group"),
            Release = GetValue(metadata, "release"),
            Weight = weight,
            Disabled = !instance.Healthy || !instance.Enabled || metadataDisabled,
            Methods = methods,
            RegistryUrl = uri.ToString()
        };
    }

    private static string? GetValue(IReadOnlyDictionary<string, string> values, string key) =>
        values.FirstOrDefault(item =>
            string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase)).Value is { Length: > 0 } value
            ? value
            : null;

    private static NacosConnectionOptions ParseConnectionString(string connectionString)
    {
        string remainder = connectionString["nacos://".Length..];
        int queryIndex = remainder.IndexOf('?');
        string serverPart = queryIndex < 0 ? remainder : remainder[..queryIndex];
        string query = queryIndex < 0 ? string.Empty : remainder[(queryIndex + 1)..];
        List<string> serverAddresses = serverPart
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(address => address.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                               || address.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? address.TrimEnd('/') + "/"
                : "http://" + address.TrimEnd('/') + "/")
            .ToList();
        if (serverAddresses.Count == 0)
        {
            throw new ArgumentException("Nacos 连接地址中未包含服务器。", nameof(connectionString));
        }

        Dictionary<string, string> values = ParseQuery(query);
        return new NacosConnectionOptions(
            serverAddresses,
            Value(values, "namespace") ?? Value(values, "namespaceId") ?? string.Empty,
            Value(values, "username"),
            Value(values, "password"),
            Value(values, "accessKey"),
            Value(values, "secretKey"));
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = part.Split('=', 2);
            result[Uri.UnescapeDataString(pair[0].Replace('+', ' '))] = pair.Length == 2
                ? Uri.UnescapeDataString(pair[1].Replace('+', ' '))
                : string.Empty;
        }
        return result;
    }

    private static string? Value(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string? value) ? value : null;

    private static string NormalizeGroup(string? group)
    {
        string normalized = group?.Trim().Trim('/') ?? string.Empty;
        return normalized.Length == 0 ? "DEFAULT_GROUP" : normalized;
    }

    private sealed record NacosConnectionOptions(
        List<string> ServerAddresses,
        string Namespace,
        string? UserName,
        string? Password,
        string? AccessKey,
        string? SecretKey);
}
