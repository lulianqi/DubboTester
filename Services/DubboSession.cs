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
    private MyZookeeper? _registry;
    private string? _configurationKey;
    private bool _disposed;

    public async Task<DubboCatalogLoadResult> LoadServicesAsync(
        TesterSettings settings,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        string root = NormalizeRoot(settings.RegistryRootPath);
        return await catalogCache.GetOrLoadAsync(
            settings.ZooKeeperAddress,
            root,
            requireAllServiceDetails: false,
            forceRefresh,
            async _ =>
            {
                await EnsureConfiguredAsync(settings, cancellationToken);
                return await LoadServiceNamesAsync(root, cancellationToken);
            },
            cancellationToken);
    }

    public async Task LoadServiceDetailsAsync(
        TesterSettings settings,
        DubboServiceItem service,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        string root = NormalizeRoot(settings.RegistryRootPath);
        if (!forceRefresh
            && catalogCache.TryApplyServiceDetails(
                settings.ZooKeeperAddress,
                root,
                service))
        {
            return;
        }

        await EnsureConfiguredAsync(settings, cancellationToken);
        await LoadServiceDetailsCoreAsync(
            root,
            service,
            retryTime: 2,
            cancellationToken);
        catalogCache.UpdateService(settings.ZooKeeperAddress, root, service);
    }

    private async Task<IReadOnlyList<DubboServiceItem>> LoadServiceNamesAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var result = await _registry!.GetChildrenAsync(root).WaitAsync(cancellationToken);
        return (result?.Children ?? [])
            .Where(IsServiceNode)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new DubboServiceItem { Name = name })
            .ToArray();
    }

    private async Task LoadServiceDetailsCoreAsync(
        string root,
        DubboServiceItem service,
        int retryTime,
        CancellationToken cancellationToken)
    {
        string providersPath = $"{root}/{service.Name}/providers";
        var providers = await _registry!
            .GetChildrenAsync(providersPath, retryTime: retryTime)
            .WaitAsync(cancellationToken);
        PopulateServiceDetails(service, providers?.Children ?? []);
    }

    private static bool IsServiceNode(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && !string.Equals(name, "metadata", StringComparison.Ordinal);

    internal static void PopulateServiceDetails(
        DubboServiceItem service,
        IEnumerable<string> providerNodes)
    {
        service.Providers.Clear();
        service.Methods.Clear();

        foreach (string providerNode in providerNodes)
        {
            DubboProviderItem? provider = ParseProvider(providerNode, service.Name);
            if (provider is null)
            {
                continue;
            }

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
        CancellationToken cancellationToken = default)
    {
        await EnsureConfiguredAsync(settings, cancellationToken);
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
            await EnsureConfiguredAsync(settings, cancellationToken);
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
            result = await _client!.QueryGenericAsync(invocation);
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
            await EnsureConfiguredAsync(settings, cancellationToken);
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

    private async Task EnsureConfiguredAsync(
        TesterSettings settings,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(settings.ZooKeeperAddress))
        {
            throw new ArgumentException("请先在设置中填写 ZooKeeper 连接地址。");
        }

        string configurationKey = JsonSerializer.Serialize(new
        {
            settings.ZooKeeperAddress,
            Root = NormalizeRoot(settings.RegistryRootPath),
            settings.MetadataCenterAddress,
            MetadataRoot = NormalizeRoot(settings.MetadataRootPath),
            settings.RequestTimeoutMs,
            settings.MetadataFallbackTimeoutMs,
            settings.LoadBalanceMode
        });

        if (_configurationKey == configurationKey && _client is not null && _registry is not null)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_configurationKey == configurationKey && _client is not null && _registry is not null)
            {
                return;
            }

            _client?.Dispose();
            _registry?.Dispose();

            string registryRoot = NormalizeRoot(settings.RegistryRootPath);
            _registry = new MyZookeeper(settings.ZooKeeperAddress.Trim());
            _client = new DubboClient(
                settings.ZooKeeperAddress.Trim(),
                new DubboClient.DubboClientConf
                {
                    DubboRootPath = registryRoot + "/",
                    MetadataCenterAddress = NullIfWhiteSpace(settings.MetadataCenterAddress),
                    MetadataRootPath = NormalizeRoot(settings.MetadataRootPath),
                    DubboRequestTimeout = Math.Clamp(settings.RequestTimeoutMs, 100, 600_000),
                    TelnetMetadataTimeout = Math.Clamp(
                        settings.MetadataFallbackTimeoutMs,
                        100,
                        60_000),
                    MaintainServiceNum = 50,
                    NowLoadBalanceMode = settings.LoadBalanceMode
                });
            _configurationKey = configurationKey;
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

    private static string NormalizeRoot(string? root)
    {
        string value = string.IsNullOrWhiteSpace(root) ? "/dubbo" : root.Trim();
        if (!value.StartsWith('/'))
        {
            value = "/" + value;
        }

        return value.Length > 1 ? value.TrimEnd('/') : value;
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
        _registry?.Dispose();
        _gate.Dispose();
    }
}
