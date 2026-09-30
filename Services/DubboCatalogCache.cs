using System.Collections.Concurrent;
using DubboTester.Models;

namespace DubboTester.Services;

/// <summary>
/// Process-wide LRU cache for registry catalogs. Cached models are always cloned so
/// page-specific selection and expansion state can never leak between users.
/// </summary>
public sealed class DubboCatalogCache
{
    private const int MaximumAddressCount = 10;
    private readonly object _sync = new();
    private readonly Dictionary<string, CacheEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _addressLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private long _accessSequence;

    public async Task<DubboCatalogLoadResult> GetOrLoadAsync(
        string zooKeeperAddress,
        string registryRoot,
        bool requireAllServiceDetails,
        bool forceRefresh,
        Func<IReadOnlyList<DubboServiceItem>?, Task<IReadOnlyList<DubboServiceItem>>> loader,
        CancellationToken cancellationToken = default)
    {
        string address = NormalizeAddress(zooKeeperAddress);
        if (!forceRefresh
            && TryGet(address, registryRoot, requireAllServiceDetails, out DubboCatalogLoadResult cached))
        {
            return cached;
        }

        SemaphoreSlim addressLock = _addressLocks.GetOrAdd(address, static _ => new SemaphoreSlim(1, 1));
        await addressLock.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh
                && TryGet(address, registryRoot, requireAllServiceDetails, out cached))
            {
                return cached;
            }

            IReadOnlyList<DubboServiceItem>? seed = forceRefresh
                ? null
                : GetSnapshot(address, registryRoot);
            IReadOnlyList<DubboServiceItem> loaded = await loader(seed);
            IReadOnlyList<DubboServiceItem> snapshot = CloneServices(loaded);
            Store(address, registryRoot, snapshot, requireAllServiceDetails);
            return new DubboCatalogLoadResult(
                CloneServices(snapshot),
                FromCache: false,
                HasAllServiceDetails: snapshot.All(service => service.IsLoaded),
                PreloadAttempted: requireAllServiceDetails);
        }
        finally
        {
            addressLock.Release();
        }
    }

    public void UpdateService(
        string zooKeeperAddress,
        string registryRoot,
        DubboServiceItem service)
    {
        string address = NormalizeAddress(zooKeeperAddress);
        lock (_sync)
        {
            if (!_entries.TryGetValue(address, out CacheEntry? entry)
                || !string.Equals(entry.RegistryRoot, registryRoot, StringComparison.Ordinal))
            {
                return;
            }

            int index = entry.Services.FindIndex(item =>
                string.Equals(item.Name, service.Name, StringComparison.Ordinal));
            if (index < 0)
            {
                return;
            }

            entry.Services[index] = CloneService(service);
            entry.LastAccess = NextAccessSequence();
        }
    }

    public bool TryApplyServiceDetails(
        string zooKeeperAddress,
        string registryRoot,
        DubboServiceItem target)
    {
        string address = NormalizeAddress(zooKeeperAddress);
        lock (_sync)
        {
            if (!_entries.TryGetValue(address, out CacheEntry? entry)
                || !string.Equals(entry.RegistryRoot, registryRoot, StringComparison.Ordinal))
            {
                return false;
            }

            DubboServiceItem? cached = entry.Services.FirstOrDefault(item =>
                item.IsLoaded
                && string.Equals(item.Name, target.Name, StringComparison.Ordinal));
            if (cached is null)
            {
                return false;
            }

            DubboServiceItem snapshot = CloneService(cached);
            target.Providers.Clear();
            target.Providers.AddRange(snapshot.Providers);
            target.Methods.Clear();
            target.Methods.AddRange(snapshot.Methods);
            target.IsLoaded = true;
            entry.LastAccess = NextAccessSequence();
            return true;
        }
    }

    public void MarkPreloadAttempted(string zooKeeperAddress, string registryRoot)
    {
        string address = NormalizeAddress(zooKeeperAddress);
        lock (_sync)
        {
            if (!_entries.TryGetValue(address, out CacheEntry? entry)
                || !string.Equals(entry.RegistryRoot, registryRoot, StringComparison.Ordinal))
            {
                return;
            }

            entry.PreloadAttempted = true;
            entry.LastAccess = NextAccessSequence();
        }
    }

    private bool TryGet(
        string address,
        string registryRoot,
        bool requireAllServiceDetails,
        out DubboCatalogLoadResult result)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(address, out CacheEntry? entry)
                || !string.Equals(entry.RegistryRoot, registryRoot, StringComparison.Ordinal)
                || (requireAllServiceDetails && !entry.PreloadAttempted))
            {
                result = null!;
                return false;
            }

            entry.LastAccess = NextAccessSequence();
            bool complete = entry.Services.All(service => service.IsLoaded);
            result = new DubboCatalogLoadResult(
                CloneServices(entry.Services),
                FromCache: true,
                HasAllServiceDetails: complete,
                PreloadAttempted: entry.PreloadAttempted);
            return true;
        }
    }

    private IReadOnlyList<DubboServiceItem>? GetSnapshot(
        string address,
        string registryRoot)
    {
        lock (_sync)
        {
            return _entries.TryGetValue(address, out CacheEntry? entry)
                && string.Equals(entry.RegistryRoot, registryRoot, StringComparison.Ordinal)
                    ? CloneServices(entry.Services)
                    : null;
        }
    }

    private void Store(
        string address,
        string registryRoot,
        IReadOnlyList<DubboServiceItem> services,
        bool preloadAttempted)
    {
        lock (_sync)
        {
            if (!_entries.ContainsKey(address) && _entries.Count >= MaximumAddressCount)
            {
                string leastRecentlyUsedAddress = _entries
                    .MinBy(pair => pair.Value.LastAccess)
                    .Key;
                _entries.Remove(leastRecentlyUsedAddress);
            }

            _entries[address] = new CacheEntry(
                registryRoot,
                CloneServices(services).ToList(),
                preloadAttempted,
                NextAccessSequence());
        }
    }

    private long NextAccessSequence() => Interlocked.Increment(ref _accessSequence);

    private static string NormalizeAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new ArgumentException("请先在设置中填写 ZooKeeper 连接地址。", nameof(address));
        }

        return address.Trim();
    }

    private static IReadOnlyList<DubboServiceItem> CloneServices(
        IEnumerable<DubboServiceItem> services) =>
        services.Select(CloneService).ToArray();

    private static DubboServiceItem CloneService(DubboServiceItem source)
    {
        var clone = new DubboServiceItem
        {
            Name = source.Name,
            IsLoaded = source.IsLoaded
        };
        clone.Methods.AddRange(source.Methods);
        clone.Providers.AddRange(source.Providers.Select(CloneProvider));
        return clone;
    }

    private static DubboProviderItem CloneProvider(DubboProviderItem source) => new()
    {
        Host = source.Host,
        Port = source.Port,
        Scheme = source.Scheme,
        ServiceName = source.ServiceName,
        Application = source.Application,
        Version = source.Version,
        Group = source.Group,
        Release = source.Release,
        Weight = source.Weight,
        Disabled = source.Disabled,
        Methods = [.. source.Methods],
        RegistryUrl = source.RegistryUrl
    };

    private sealed class CacheEntry(
        string registryRoot,
        List<DubboServiceItem> services,
        bool preloadAttempted,
        long lastAccess)
    {
        public string RegistryRoot { get; } = registryRoot;
        public List<DubboServiceItem> Services { get; } = services;
        public bool PreloadAttempted { get; set; } = preloadAttempted;
        public long LastAccess { get; set; } = lastAccess;
    }
}
