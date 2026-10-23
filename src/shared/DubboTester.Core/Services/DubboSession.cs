using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using DubboNet.Clients;
using DubboNet.Clients.DataModle;
using DubboNet.Clients.RegistryClient;
using DubboNet.DubboService;
using DubboNet.DubboService.DataModle;
using DubboTester.Models;

namespace DubboTester.Services;

public sealed class DubboSession(DubboCatalogCache catalogCache) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, Lazy<NativeDubboActuatorSuite>> _directSuites = new();
    private DubboClient? _client;
    private MyZookeeper? _zooKeeperRegistry;
    private NacosRegistryCatalogClient? _nacosRegistry;
    private string? _clientConfigurationKey;
    private string? _registryConfigurationKey;
    private bool _disposed;

    internal DubboSession CreateInvocationSession() => new(catalogCache);

    public async Task<DubboCatalogLoadResult> LoadServicesAsync(
        TesterSettings settings,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        RegistryRootSelection selection = RegistryRootPaths.Parse(settings.RegistryRootPath);
        return await catalogCache.GetOrLoadAsync(
            settings.RegistryCacheAddress,
            selection.CacheKey,
            requireAllServiceDetails: false,
            forceRefresh,
            async _ =>
            {
                await EnsureRegistryAsync(settings, cancellationToken);
                return await LoadCatalogAsync(settings.RegistryType, selection, cancellationToken);
            },
            cancellationToken);
    }

    public async Task LoadServiceDetailsAsync(
        TesterSettings settings,
        DubboServiceItem service,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        RegistryRootSelection selection = RegistryRootPaths.Parse(settings.RegistryRootPath);
        string serviceRoot = RegistryRootPaths.ResolveInvocationRoot(
            settings.RegistryRootPath,
            service.RegistryRootPath);
        if (!forceRefresh
            && catalogCache.TryApplyServiceDetails(
                settings.RegistryCacheAddress,
                selection.CacheKey,
                service))
        {
            return;
        }

        await EnsureRegistryAsync(settings, cancellationToken);
        await LoadServiceDetailsCoreAsync(
            settings.RegistryType,
            serviceRoot,
            service,
            retryTime: 2,
            cancellationToken);
        catalogCache.UpdateService(settings.RegistryCacheAddress, selection.CacheKey, service);
    }

    private async Task<IReadOnlyList<DubboServiceItem>> LoadCatalogAsync(
        RegistryCenterType registryType,
        RegistryRootSelection selection,
        CancellationToken cancellationToken)
    {
        var roots = new HashSet<string>(selection.ExplicitRoots, StringComparer.Ordinal);
        if (selection.AutoDiscover)
        {
            if (registryType == RegistryCenterType.Nacos)
            {
                roots.Add(RegistryConnectionSettings.RegistryRoot(registryType, "/"));
            }
            else
            {
                foreach (string discovered in await DiscoverRegistryRootsAsync(cancellationToken))
                {
                    roots.Add(discovered);
                }
            }
        }

        var services = new List<DubboServiceItem>();
        Exception? firstFailure = null;
        int loadedRootCount = 0;
        foreach (string root in roots.Order(StringComparer.Ordinal))
        {
            try
            {
                services.AddRange(await LoadServiceNamesAsync(registryType, root, cancellationToken));
                loadedRootCount++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                firstFailure ??= exception;
            }
        }
        if (loadedRootCount == 0 && firstFailure is not null) throw firstFailure;
        return services;
    }

    private async Task<IReadOnlyList<DubboServiceItem>> LoadServiceNamesAsync(
        RegistryCenterType registryType,
        string root,
        CancellationToken cancellationToken)
    {
        if (registryType == RegistryCenterType.Nacos)
        {
            IReadOnlyList<string> names = await _nacosRegistry!.GetServiceNamesAsync(
                RegistryConnectionSettings.NacosGroup(root),
                cancellationToken);
            return names.Select(name => new DubboServiceItem
            {
                Name = name,
                RegistryRootPath = RegistryConnectionSettings.RegistryRoot(registryType, root)
            }).ToArray();
        }

        var result = await _zooKeeperRegistry!.GetChildrenAsync(root).WaitAsync(cancellationToken);
        return ServiceNames(result?.Children ?? [])
            .Select(name => new DubboServiceItem
            {
                Name = name,
                RegistryRootPath = root
            })
            .ToArray();
    }

    private async Task<IReadOnlyList<string>> DiscoverRegistryRootsAsync(
        CancellationToken cancellationToken)
    {
        var rootResult = await _zooKeeperRegistry!
            .GetChildrenAsync("/", retryTime: 1)
            .WaitAsync(cancellationToken);
        var discovered = new List<string>();
        foreach (string child in rootResult?.Children ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            string candidate = RegistryRootPaths.NormalizeRoot(child);
            try
            {
                var servicesResult = await _zooKeeperRegistry
                    .GetChildrenAsync(candidate, retryTime: 1)
                    .WaitAsync(cancellationToken);
                string[] serviceNames = ServiceNames(servicesResult?.Children ?? []).ToArray();
                foreach (string serviceName in serviceNames)
                {
                    var branches = await _zooKeeperRegistry
                        .GetChildrenAsync($"{candidate}/{serviceName}", retryTime: 1)
                        .WaitAsync(cancellationToken);
                    if ((branches?.Children ?? []).Contains("providers", StringComparer.Ordinal))
                    {
                        discovered.Add(candidate);
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // A first-level node that cannot be read as a Dubbo registry is ignored.
            }
        }
        return discovered;
    }

    private static IEnumerable<string> ServiceNames(IEnumerable<string> names) => names
            .Where(IsServiceNode)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Distinct(StringComparer.Ordinal);

    private async Task LoadServiceDetailsCoreAsync(
        RegistryCenterType registryType,
        string root,
        DubboServiceItem service,
        int retryTime,
        CancellationToken cancellationToken)
    {
        if (registryType == RegistryCenterType.Nacos)
        {
            IReadOnlyList<DubboProviderItem> nacosProviders = await _nacosRegistry!.GetProvidersAsync(
                service.Name,
                RegistryConnectionSettings.NacosGroup(root),
                cancellationToken);
            PopulateServiceDetails(service, nacosProviders);
            return;
        }

        string providersPath = $"{root}/{service.Name}/providers";
        var providers = await _zooKeeperRegistry!
            .GetChildrenAsync(providersPath, retryTime: retryTime)
            .WaitAsync(cancellationToken);
        PopulateServiceDetails(service, providers?.Children ?? []);
    }

    private static bool IsServiceNode(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && !RegistryManagementNodes.Contains(name);

    private static readonly HashSet<string> RegistryManagementNodes = new(
        ["config", "metadata", "mapping", "service-discovery"],
        StringComparer.OrdinalIgnoreCase);

    internal static void PopulateServiceDetails(
        DubboServiceItem service,
        IEnumerable<string> providerNodes)
    {
        PopulateServiceDetails(
            service,
            providerNodes.Select(providerNode => ParseProvider(providerNode, service.Name))
                .OfType<DubboProviderItem>());
    }

    internal static void PopulateServiceDetails(
        DubboServiceItem service,
        IEnumerable<DubboProviderItem> providers)
    {
        service.Providers.Clear();
        service.Methods.Clear();

        foreach (DubboProviderItem provider in providers)
        {
            service.Providers.Add(provider);
            foreach (string method in provider.Methods)
            {
                if (!service.Methods.Contains(method, StringComparer.Ordinal))
                {
                    service.Methods.Add(method);
                }
            }
        }

        service.Providers.Sort((left, right) =>
            string.Compare(left.Address, right.Address, StringComparison.OrdinalIgnoreCase));
        service.Methods.Sort(StringComparer.OrdinalIgnoreCase);
        service.IsLoaded = true;
    }

    public async Task<IReadOnlyList<MethodSignatureItem>> LoadMethodSignaturesAsync(
        TesterSettings settings,
        string serviceName,
        string methodName,
        string? registryRootPath = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureConfiguredAsync(settings, registryRootPath, cancellationToken);
        IReadOnlyList<DubboMethodMetadata> metadata =
            await _client!.GetMethodMetadataAsync(serviceName, methodName);
        IReadOnlyList<DubboTypeMetadata> serviceTypes = [];
        if (metadata.Any(method => method.MetadataSource == DubboMetadataSource.MetadataCenter))
        {
            serviceTypes = await _client.GetServiceTypesMetadataAsync(serviceName);
        }

        return metadata
            .Select(method => MethodSignatureItem.FromMetadata(method, serviceTypes))
            .ToArray();
    }

    public async Task<InvocationOutput> InvokeAsync(
        TesterSettings settings,
        InvocationInput input,
        CancellationToken cancellationToken = default)
    {
        ValidateInvocation(input);
        if (input.Route == InvocationRoute.Registry)
        {
            await EnsureConfiguredAsync(settings, input.RegistryRootPath, cancellationToken);
        }

        object?[] arguments = JsonArgumentParser.ParseArguments(input.RequestJson);
        string[] parameterTypes = JsonArgumentParser.ParseJavaTypes(input.JavaParameterTypes);
        if (parameterTypes.Length > 0 && parameterTypes.Length != arguments.Length)
        {
            throw new ArgumentException(
                $"Java 参数类型数量（{parameterTypes.Length}）与请求参数数量（{arguments.Length}）不一致。");
        }

        var invocation = new DubboInvocation(
            input.ServiceName.Trim(),
            input.MethodName.Trim(),
            parameterTypes,
            arguments)
        {
            Version = NullIfWhiteSpace(input.Version),
            Group = NullIfWhiteSpace(input.Group),
            Attachments = JsonArgumentParser.ParseAttachments(input.AttachmentsJson)
        };

        DubboRequestResult result;
        if (input.Route == InvocationRoute.DirectProvider)
        {
            if (parameterTypes.Length == 0 && arguments.Length > 0)
            {
                throw new ArgumentException("直连 Provider 时请填写精确的 Java 参数类型，避免错误推断。");
            }

            NativeDubboActuatorSuite suite = GetDirectSuite(settings, input);
            result = await suite.SendQuery(invocation, cancellationToken);
        }
        else
        {
            result = await _client!.QueryAsync(invocation);
        }

        string content = result.QuerySuccess
            ? JsonArgumentParser.PrettyPrint(result.Result)
            : string.Empty;
        return new InvocationOutput
        {
            Success = result.QuerySuccess,
            Content = content,
            Error = result.ErrorMeaasge,
            RequestElapsed = result.RequestElapsed,
            RequestId = result.RequestId,
            ResponseStatus = result.ResponseStatus,
            SerializationId = result.SerializationId,
            Attachments = result.ResponseAttachments
        };
    }

    public async Task PrepareForInvocationsAsync(
        TesterSettings settings,
        InvocationInput input,
        CancellationToken cancellationToken = default)
    {
        ValidateInvocation(input);
        if (input.Route == InvocationRoute.Registry)
        {
            await EnsureConfiguredAsync(settings, input.RegistryRootPath, cancellationToken);
            return;
        }

        _ = GetDirectSuite(settings, input);
    }

    public static async Task TestProviderAsync(
        string host,
        int port,
        int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("请先填写 Provider 地址。");
        }

        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Math.Clamp(timeoutMs, 500, 60_000));
        await client.ConnectAsync(host.Trim(), port, timeout.Token);
    }

    public static async Task TestZooKeeperAsync(
        string address,
        int timeoutMs = 10_000,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new ArgumentException("请先填写 ZooKeeper 地址。", nameof(address));
        }

        string normalizedAddress = address.Trim();
        int effectiveTimeoutMs = Math.Clamp(timeoutMs, 1_000, 30_000);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(effectiveTimeoutMs);

        try
        {
            await using var registry = new MyZookeeper(normalizedAddress);
            bool connected = await registry.ConnectZooKeeperAsync(timeout.Token);
            if (!connected || !registry.IsConnected)
            {
                throw new InvalidOperationException("ZooKeeper 未能建立有效会话。");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"连接 ZooKeeper {normalizedAddress} 超时（{effectiveTimeoutMs} ms）。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"无法连接 ZooKeeper {normalizedAddress}：{exception.Message}",
                exception);
        }
    }

    public static async Task TestRegistryAsync(
        TesterSettings settings,
        int timeoutMs = 10_000,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settings.RegistryAddress))
        {
            throw new ArgumentException($"请先填写 {settings.RegistryDisplayName} 地址。");
        }

        if (settings.RegistryType == RegistryCenterType.ZooKeeper)
        {
            await TestZooKeeperAsync(settings.RegistryAddress, timeoutMs, cancellationToken);
            return;
        }

        string address = settings.RegistryConnectionString;
        int effectiveTimeoutMs = Math.Clamp(timeoutMs, 1_000, 30_000);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(effectiveTimeoutMs);
        try
        {
            NacosRegistryCatalogClient client = NacosRegistryCatalogPool.Shared.Get(address);
            string group = RegistryConnectionSettings.NacosGroup(
                RegistryRootPaths.Parse(settings.RegistryRootPath).ExplicitRoots.FirstOrDefault());
            await client.TestAsync(group, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"连接 Nacos {settings.RegistryAddress.Trim()} 超时（{effectiveTimeoutMs} ms）。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"无法连接 Nacos {settings.RegistryAddress.Trim()}：{exception.Message}",
                exception);
        }
    }

    private async Task EnsureRegistryAsync(
        TesterSettings settings,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(settings.RegistryAddress))
        {
            throw new ArgumentException($"请先在设置中填写 {settings.RegistryDisplayName} 连接地址。");
        }

        string address = settings.RegistryConnectionString;
        string configurationKey = $"{settings.RegistryType}\n{address}";
        bool configured = settings.RegistryType == RegistryCenterType.Nacos
            ? _nacosRegistry is not null
            : _zooKeeperRegistry is not null;
        if (configured
            && string.Equals(_registryConfigurationKey, configurationKey, StringComparison.Ordinal))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            configured = settings.RegistryType == RegistryCenterType.Nacos
                ? _nacosRegistry is not null
                : _zooKeeperRegistry is not null;
            if (configured
                && string.Equals(_registryConfigurationKey, configurationKey, StringComparison.Ordinal))
            {
                return;
            }

            _client?.Dispose();
            _zooKeeperRegistry?.Dispose();
            _client = null;
            _clientConfigurationKey = null;
            _zooKeeperRegistry = null;
            _nacosRegistry = null;
            if (settings.RegistryType == RegistryCenterType.Nacos)
            {
                _nacosRegistry = NacosRegistryCatalogPool.Shared.Get(address);
            }
            else
            {
                _zooKeeperRegistry = new MyZookeeper(address);
            }
            _registryConfigurationKey = configurationKey;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureConfiguredAsync(
        TesterSettings settings,
        string? registryRootPath,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(settings.RegistryAddress))
        {
            throw new ArgumentException($"请先在设置中填写 {settings.RegistryDisplayName} 连接地址。");
        }

        string address = settings.RegistryConnectionString;
        string registryRoot = RegistryRootPaths.ResolveInvocationRoot(
            settings.RegistryRootPath,
            registryRootPath);
        string? metadataAddress = RegistryConnectionSettings.BuildMetadataConnectionString(settings);
        string metadataRoot = RegistryConnectionSettings.ResolveMetadataRoot(
            settings,
            registryRoot,
            metadataAddress);
        string configurationKey = JsonSerializer.Serialize(new
        {
            settings.RegistryType,
            RegistryAddress = address,
            Root = registryRoot,
            MetadataCenterAddress = metadataAddress,
            MetadataRoot = metadataRoot,
            settings.RequestTimeoutMs,
            settings.MetadataFallbackTimeoutMs,
            settings.LoadBalanceMode
        });

        if (_clientConfigurationKey == configurationKey && _client is not null)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_clientConfigurationKey == configurationKey && _client is not null)
            {
                return;
            }

            _client?.Dispose();
            _client = new DubboClient(
                address,
                new DubboClient.DubboClientConf
                {
                    DubboRootPath = registryRoot == "/" ? "/" : registryRoot + "/",
                    MetadataCenterAddress = metadataAddress,
                    MetadataRootPath = metadataRoot,
                    DubboRequestTimeout = Math.Clamp(settings.RequestTimeoutMs, 100, 600_000),
                    TelnetMetadataTimeout = Math.Clamp(
                        settings.MetadataFallbackTimeoutMs,
                        100,
                        60_000),
                    MaintainServiceNum = 50,
                    NowLoadBalanceMode = settings.LoadBalanceMode
                });
            _clientConfigurationKey = configurationKey;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static DubboProviderItem? ParseProvider(string registryNode, string fallbackService)
    {
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(registryNode);
        }
        catch (UriFormatException)
        {
            return null;
        }

        if (!Uri.TryCreate(decoded, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        if (uri.Port is < 1 or > 65_535
            || (uri.Scheme != "dubbo" && uri.Scheme != "tri"))
        {
            return null;
        }

        Dictionary<string, string> query = ParseQuery(uri.Query);
        string serviceName = query.GetValueOrDefault("interface")
            ?? uri.AbsolutePath.TrimStart('/')
            ?? fallbackService;
        string[] methods = (query.GetValueOrDefault("methods") ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new DubboProviderItem
        {
            Host = uri.Host,
            Port = uri.Port,
            Scheme = uri.Scheme,
            ServiceName = string.IsNullOrWhiteSpace(serviceName) ? fallbackService : serviceName,
            Application = query.GetValueOrDefault("application"),
            Version = query.GetValueOrDefault("version"),
            Group = query.GetValueOrDefault("group"),
            Release = query.GetValueOrDefault("release"),
            Weight = int.TryParse(query.GetValueOrDefault("weight"), out int weight) ? weight : 100,
            Disabled = bool.TryParse(query.GetValueOrDefault("disabled"), out bool disabled) && disabled,
            Methods = methods,
            RegistryUrl = decoded
        };
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = part.Split('=', 2);
            string key = Uri.UnescapeDataString(pair[0].Replace('+', ' '));
            string value = pair.Length == 2
                ? Uri.UnescapeDataString(pair[1].Replace('+', ' '))
                : string.Empty;
            result[key] = value;
        }

        return result;
    }

    private NativeDubboActuatorSuite GetDirectSuite(
        TesterSettings settings,
        InvocationInput input)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string host = input.Host.Trim();
        string service = input.ServiceName.Trim();
        int timeout = Math.Clamp(settings.RequestTimeoutMs, 100, 600_000);
        string key = $"{host}|{input.Port}|{service}|{timeout}";
        return _directSuites.GetOrAdd(
            key,
            _ => new Lazy<NativeDubboActuatorSuite>(
                () => new NativeDubboActuatorSuite(
                    host,
                    input.Port,
                    new DubboActuatorSuiteConf
                    {
                        DubboRequestTimeout = timeout,
                        DefaultServiceName = service
                    }),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private static void ValidateInvocation(InvocationInput input)
    {
        if (string.IsNullOrWhiteSpace(input.ServiceName))
        {
            throw new ArgumentException("请选择或填写 Dubbo 服务。");
        }

        if (string.IsNullOrWhiteSpace(input.MethodName))
        {
            throw new ArgumentException("请选择或填写 Dubbo 方法。");
        }

        if (input.Route == InvocationRoute.DirectProvider
            && (string.IsNullOrWhiteSpace(input.Host) || input.Port is < 1 or > 65_535))
        {
            throw new ArgumentException("直连 Provider 的地址或端口无效。");
        }
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (Lazy<NativeDubboActuatorSuite> suite in _directSuites.Values)
        {
            if (suite.IsValueCreated) suite.Value.Dispose();
        }
        _directSuites.Clear();
        _client?.Dispose();
        _zooKeeperRegistry?.Dispose();
        _gate.Dispose();
    }
}
