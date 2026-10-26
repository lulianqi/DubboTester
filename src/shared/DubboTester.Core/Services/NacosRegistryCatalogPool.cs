using DubboNet.Clients.DataModle;
using DubboNet.Clients.RegistryClient;
using DubboTester.Models;

namespace DubboTester.Services;

/// <summary>
/// Reuses the DubboNet Nacos catalog clients used to build the tester's service tree.
/// DubboNet owns all Nacos SDK interaction and exposes only its own public models here.
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
        string key = MyNacos.NormalizeConnectionString(connectionString);
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
}

internal sealed class NacosRegistryCatalogClient
{
    private readonly MyNacos _nacos;

    public NacosRegistryCatalogClient(string connectionString)
    {
        _nacos = new MyNacos(connectionString);
    }

    public Task<IReadOnlyList<string>> DiscoverGroupsAsync(
        CancellationToken cancellationToken = default) =>
        _nacos.ListRegistryRootsAsync(cancellationToken);

    public Task<IReadOnlyList<string>> GetServiceNamesAsync(
        string group,
        CancellationToken cancellationToken = default) =>
        _nacos.ListServicesAsync(group, cancellationToken);

    public async Task<IReadOnlyList<DubboProviderItem>> GetProvidersAsync(
        string interfaceName,
        string group,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DubboServiceEndPointInfo> providers = await _nacos.GetProvidersAsync(
            interfaceName,
            group,
            cancellationToken);
        return providers.Select(provider => ConvertProvider(provider, interfaceName)).ToArray();
    }

    public Task TestAsync(
        string group,
        CancellationToken cancellationToken = default) =>
        _nacos.TestConnectionAsync(group, cancellationToken);

    internal static DubboProviderItem ConvertProvider(
        DubboServiceEndPointInfo provider,
        string fallbackInterfaceName)
    {
        string[] methods = (provider.Methods ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new DubboProviderItem
        {
            Host = provider.EndPoint.Address.ToString(),
            Port = provider.EndPoint.Port,
            Scheme = provider.Scheme?.ToLowerInvariant() ?? "dubbo",
            ServiceName = string.IsNullOrWhiteSpace(provider.Interface)
                ? fallbackInterfaceName
                : provider.Interface,
            Application = provider.Application,
            Version = provider.Version,
            Group = provider.Group,
            Release = provider.Release,
            Weight = provider.Weight,
            Disabled = provider.Disabled ?? false,
            Methods = methods,
            RegistryUrl = provider.RegistryUrl ?? string.Empty
        };
    }
}
