using System.Collections.Concurrent;
using DubboNet.Clients.RegistryClient;
using DubboTester.Models;

namespace DubboTester.Services;

/// <summary>
/// Owns process-wide catalog preloads. A page observes an operation but does not own
/// it, so disconnected Blazor circuits cannot hold the normal catalog cache path.
/// </summary>
public sealed class DubboCatalogPreloadService(DubboCatalogCache catalogCache) : IDisposable
{
    private const int MaximumTrackedAddresses = 10;
    private const int PreloadConcurrency = 6;
    private const int ConsecutiveFailureLimit = 5;
    private static readonly TimeSpan PreloadTimeout = TimeSpan.FromMinutes(5);

    private readonly object _sync = new();
    private readonly Dictionary<string, PreloadOperation> _operations =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public CatalogPreloadStatus StartOrGet(
        TesterSettings settings,
        IReadOnlyList<DubboServiceItem> services)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string address = NormalizeAddress(settings.ZooKeeperAddress);
        string root = NormalizeRoot(settings.RegistryRootPath);
        string key = BuildKey(address, root);
        PreloadOperation operation;

        lock (_sync)
        {
            if (_operations.TryGetValue(key, out PreloadOperation? existing)
                && existing.State == CatalogPreloadState.Running)
            {
                return existing.GetStatus();
            }

            operation = new PreloadOperation(address, root, services);
            _operations[key] = operation;
            TrimCompletedOperations();
        }

        operation.Execution = RunAsync(operation);
        return operation.GetStatus();
    }

    public CatalogPreloadStatus? GetStatus(string zooKeeperAddress, string registryRoot)
    {
        string key = BuildKey(NormalizeAddress(zooKeeperAddress), NormalizeRoot(registryRoot));
        lock (_sync)
        {
            return _operations.TryGetValue(key, out PreloadOperation? operation)
                ? operation.GetStatus()
                : null;
        }
    }

    public bool Stop(string zooKeeperAddress, string registryRoot)
    {
        string key = BuildKey(NormalizeAddress(zooKeeperAddress), NormalizeRoot(registryRoot));
        PreloadOperation? operation;
        lock (_sync)
        {
            _operations.TryGetValue(key, out operation);
        }

        return operation is not null && operation.RequestStop(CatalogPreloadState.Stopped);
    }

    private async Task RunAsync(PreloadOperation operation)
    {
        // 强制切到后台调度，确保命中目录缓存的页面线程不会同步进入 ZooKeeper 初始化。
        await Task.Yield();
        using var timeout = new CancellationTokenSource(PreloadTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            operation.Cancellation.Token,
            timeout.Token);
        try
        {
            var pending = new ConcurrentQueue<DubboServiceItem>(
                operation.Services.Where(service => !service.IsLoaded));
            if (pending.IsEmpty)
            {
                CompleteOperation(operation, CatalogPreloadState.Completed);
                return;
            }

            await using var registry = new MyZookeeper(operation.ZooKeeperAddress);

            async Task WorkerAsync()
            {
                while (!linked.IsCancellationRequested
                       && pending.TryDequeue(out DubboServiceItem? service))
                {
                    try
                    {
                        string path = $"{operation.RegistryRoot}/{service.Name}/providers";
                        var providers = await registry
                            .GetChildrenAsync(path, retryTime: 1)
                            .WaitAsync(linked.Token);
                        DubboSession.PopulateServiceDetails(service, providers?.Children ?? []);
                        catalogCache.UpdateService(
                            operation.ZooKeeperAddress,
                            operation.RegistryRoot,
                            service);
                        operation.RegisterSuccess();
                    }
                    catch (OperationCanceledException) when (linked.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"预取服务 {service.Name} 失败：{exception.Message}");
                        if (operation.RegisterFailure() >= ConsecutiveFailureLimit)
                        {
                            operation.RequestStop(CatalogPreloadState.ConsecutiveFailures);
                            return;
                        }
                    }
                }
            }

            Task[] workers = Enumerable
                .Range(0, Math.Min(PreloadConcurrency, pending.Count))
                .Select(_ => WorkerAsync())
                .ToArray();
            await Task.WhenAll(workers);

            CatalogPreloadState finalState = operation.RequestedStopState
                ?? (timeout.IsCancellationRequested
                    ? CatalogPreloadState.TimedOut
                    : CatalogPreloadState.Completed);
            CompleteOperation(operation, finalState);
        }
        catch (OperationCanceledException)
        {
            CatalogPreloadState finalState = operation.RequestedStopState
                ?? (timeout.IsCancellationRequested
                    ? CatalogPreloadState.TimedOut
                    : CatalogPreloadState.Stopped);
            CompleteOperation(operation, finalState);
        }
        catch (Exception exception)
        {
            CompleteOperation(operation, CatalogPreloadState.Faulted, exception.Message);
        }
    }

    private void CompleteOperation(
        PreloadOperation operation,
        CatalogPreloadState state,
        string? error = null)
    {
        // 先提交缓存标记，再发布终态，避免新页面在两者之间启动重复预取。
        catalogCache.MarkPreloadAttempted(
            operation.ZooKeeperAddress,
            operation.RegistryRoot);
        operation.Complete(state, error);
    }

    private void TrimCompletedOperations()
    {
        if (_operations.Count <= MaximumTrackedAddresses)
        {
            return;
        }

        foreach (string key in _operations
                     .Where(pair => pair.Value.State != CatalogPreloadState.Running)
                     .OrderBy(pair => pair.Value.StartedAt)
                     .Take(_operations.Count - MaximumTrackedAddresses)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _operations.Remove(key);
        }
    }

    private static string BuildKey(string address, string root) => $"{address}\n{root}";

    private static string NormalizeAddress(string address) =>
        string.IsNullOrWhiteSpace(address)
            ? throw new ArgumentException("请先在设置中填写 ZooKeeper 连接地址。", nameof(address))
            : address.Trim();

    private static string NormalizeRoot(string? root)
    {
        string value = string.IsNullOrWhiteSpace(root) ? "/dubbo" : root.Trim();
        if (!value.StartsWith('/')) value = "/" + value;
        return value.Length > 1 ? value.TrimEnd('/') : value;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        PreloadOperation[] operations;
        lock (_sync)
        {
            operations = _operations.Values.ToArray();
            _operations.Clear();
        }

        foreach (PreloadOperation operation in operations)
        {
            operation.RequestStop(CatalogPreloadState.Stopped);
        }
    }

    private sealed class PreloadOperation
    {
        private readonly object _sync = new();
        private int _loadedServiceCount;
        private int _failedServiceCount;
        private int _consecutiveFailureCount;
        private CatalogPreloadState _state = CatalogPreloadState.Running;
        private CatalogPreloadState? _requestedStopState;
        private string? _error;

        public PreloadOperation(
            string zooKeeperAddress,
            string registryRoot,
            IReadOnlyList<DubboServiceItem> services)
        {
            ZooKeeperAddress = zooKeeperAddress;
            RegistryRoot = registryRoot;
            Services = services.Select(CloneService).ToArray();
            _loadedServiceCount = services.Count(service => service.IsLoaded);
        }

        public string ZooKeeperAddress { get; }
        public string RegistryRoot { get; }
        public IReadOnlyList<DubboServiceItem> Services { get; }
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task? Execution { get; set; }

        public CatalogPreloadState State
        {
            get { lock (_sync) return _state; }
        }

        public CatalogPreloadState? RequestedStopState
        {
            get { lock (_sync) return _requestedStopState; }
        }

        public void RegisterSuccess()
        {
            Interlocked.Increment(ref _loadedServiceCount);
            Interlocked.Exchange(ref _consecutiveFailureCount, 0);
        }

        public int RegisterFailure()
        {
            Interlocked.Increment(ref _failedServiceCount);
            return Interlocked.Increment(ref _consecutiveFailureCount);
        }

        public bool RequestStop(CatalogPreloadState state)
        {
            lock (_sync)
            {
                if (_state != CatalogPreloadState.Running)
                {
                    return false;
                }
                _requestedStopState ??= state;
            }

            Cancellation.Cancel();
            return true;
        }

        public void Complete(CatalogPreloadState state, string? error = null)
        {
            lock (_sync)
            {
                _state = state;
                _error = error;
            }
        }

        public CatalogPreloadStatus GetStatus()
        {
            lock (_sync)
            {
                return new CatalogPreloadStatus(
                    ZooKeeperAddress,
                    RegistryRoot,
                    _state,
                    Services.Count,
                    Volatile.Read(ref _loadedServiceCount),
                    Volatile.Read(ref _failedServiceCount),
                    StartedAt,
                    _error);
            }
        }

        private static DubboServiceItem CloneService(DubboServiceItem source)
        {
            var clone = new DubboServiceItem
            {
                Name = source.Name,
                IsLoaded = source.IsLoaded
            };
            clone.Methods.AddRange(source.Methods);
            clone.Providers.AddRange(source.Providers.Select(provider => new DubboProviderItem
            {
                Host = provider.Host,
                Port = provider.Port,
                Scheme = provider.Scheme,
                ServiceName = provider.ServiceName,
                Application = provider.Application,
                Version = provider.Version,
                Group = provider.Group,
                Release = provider.Release,
                Weight = provider.Weight,
                Disabled = provider.Disabled,
                Methods = [.. provider.Methods],
                RegistryUrl = provider.RegistryUrl
            }));
            return clone;
        }
    }
}
