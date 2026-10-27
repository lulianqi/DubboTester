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
    private readonly Dictionary<string, AddressCache> _entries =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _addressLocks =
        new(StringComparer.Ordinal);
    private long _accessSequence;

    public async Task<DubboCatalogLoadResult> GetOrLoadAsync(
        string registryAddress,
        string registryRoot,
        bool requireAllServiceDetails,
        bool forceRefresh,
        Func<IReadOnlyList<DubboServiceItem>?, Task<IReadOnlyList<DubboServiceItem>>> loader,
        CancellationToken cancellationToken = default)
    {
        string address = NormalizeAddress(registryAddress);
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
            cancellationToken.ThrowIfCancellationRequested();
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
        string registryAddress,
        string registryRoot,
        DubboServiceItem service)
    {
        string address = NormalizeAddress(registryAddress);
        lock (_sync)
        {
            if (!TryGetEntry(address, registryRoot, out AddressCache? addressCache, out CacheEntry? entry))
            {
                return;
            }

            int index = entry.Services.FindIndex(item =>
                IsSameService(item, service));
            if (index < 0)
            {
                return;
            }

            entry.Services[index] = CloneService(service);
            Touch(addressCache, entry);
        }
    }

    public bool TryApplyServiceDetails(
        string registryAddress,
        string registryRoot,
        DubboServiceItem target)
    {
        string address = NormalizeAddress(registryAddress);
        lock (_sync)
        {
            if (!TryGetEntry(address, registryRoot, out AddressCache? addressCache, out CacheEntry? entry))
            {
                return false;
            }

            DubboServiceItem? cached = entry.Services.FirstOrDefault(item =>
                item.IsLoaded
                && IsSameService(item, target));
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
            Touch(addressCache, entry);
            return true;
        }
    }

    public void MarkPreloadAttempted(string registryAddress, string registryRoot)
    {
        string address = NormalizeAddress(registryAddress);
        lock (_sync)
        {
            if (!TryGetEntry(address, registryRoot, out AddressCache? addressCache, out CacheEntry? entry))
            {
                return;
            }

            entry.PreloadAttempted = true;
            Touch(addressCache, entry);
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
            if (!TryGetEntry(address, registryRoot, out AddressCache? addressCache, out CacheEntry? entry)
                || (requireAllServiceDetails && !entry.PreloadAttempted))
            {
                result = null!;
                return false;
            }

            Touch(addressCache, entry);
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
            if (!TryGetEntry(address, registryRoot, out AddressCache? addressCache, out CacheEntry? entry))
            {
                return null;
            }
            Touch(addressCache, entry);
            return CloneServices(entry.Services);
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
            if (!_entries.TryGetValue(address, out AddressCache? addressCache))
            {
                if (_entries.Count >= MaximumAddressCount)
                {
                    string leastRecentlyUsedAddress = _entries
                        .MinBy(pair => pair.Value.LastAccess)
                        .Key;
                    _entries.Remove(leastRecentlyUsedAddress);
                }
                addressCache = new AddressCache();
                _entries[address] = addressCache;
            }

            long access = NextAccessSequence();
            addressCache.Catalogs[registryRoot] = new CacheEntry(
                CloneServices(services).ToList(),
                preloadAttempted,
                access);
            addressCache.LastAccess = access;
        }
    }

    private bool TryGetEntry(
        string address,
        string registryRoot,
        out AddressCache addressCache,
        out CacheEntry entry)
    {
        if (_entries.TryGetValue(address, out addressCache!)
            && addressCache.Catalogs.TryGetValue(registryRoot, out entry!))
        {
            return true;
        }
        addressCache = null!;
        entry = null!;
        return false;
    }

    private void Touch(AddressCache addressCache, CacheEntry entry)
    {
        long access = NextAccessSequence();
        addressCache.LastAccess = access;
        entry.LastAccess = access;
    }

    private static bool IsSameService(DubboServiceItem left, DubboServiceItem right) =>
        string.Equals(left.RegistryRootPath, right.RegistryRootPath, StringComparison.Ordinal)
        && string.Equals(left.Name, right.Name, StringComparison.Ordinal);

    private long NextAccessSequence() => Interlocked.Increment(ref _accessSequence);

    private static string NormalizeAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new ArgumentException("请先在设置中填写注册中心连接地址。", nameof(address));
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
            RegistryRootPath = source.RegistryRootPath,
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

    private sealed class AddressCache
    {
        public Dictionary<string, CacheEntry> Catalogs { get; } = new(StringComparer.Ordinal);
        public long LastAccess { get; set; }
    }

    private sealed class CacheEntry(
        List<DubboServiceItem> services,
        bool preloadAttempted,
        long lastAccess)
    {
        public List<DubboServiceItem> Services { get; } = services;
        public bool PreloadAttempted { get; set; } = preloadAttempted;
        public long LastAccess { get; set; } = lastAccess;
    }
}
