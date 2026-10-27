using DubboTester.Models;
using DubboTester.Services;
using Xunit;

namespace DubboTester.Tests;

public sealed class DubboCatalogCacheTests
{
    [Fact]
    public async Task GetOrLoadAsync_DiscardsLateResultsAfterCancellation()
    {
        var cache = new DubboCatalogCache();
        using var cancellation = new CancellationTokenSource();
        var lateResponse = new TaskCompletionSource<IReadOnlyList<DubboServiceItem>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<DubboCatalogLoadResult> oldLoad = cache.GetOrLoadAsync(
            "old:2181", "/dubbo", false, false,
            _ => lateResponse.Task, cancellation.Token);

        cancellation.Cancel();
        lateResponse.SetResult([new DubboServiceItem { Name = "stale.Service" }]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldLoad);

        DubboCatalogLoadResult result = await cache.GetOrLoadAsync(
            "old:2181", "/dubbo", false, false,
            _ => Task.FromResult<IReadOnlyList<DubboServiceItem>>(
                [new DubboServiceItem { Name = "fresh.Service" }]));

        Assert.False(result.FromCache);
        Assert.Equal("fresh.Service", Assert.Single(result.Services).Name);
    }

    [Fact]
    public async Task GetOrLoadAsync_NewAddressDoesNotWaitForUnfinishedAddress()
    {
        var cache = new DubboCatalogCache();
        var oldResponse = new TaskCompletionSource<IReadOnlyList<DubboServiceItem>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<DubboCatalogLoadResult> oldLoad = cache.GetOrLoadAsync(
            "slow:2181", "/dubbo", false, false, _ => oldResponse.Task);
        try
        {
            DubboCatalogLoadResult newResult = await cache.GetOrLoadAsync(
                "new:2181", "/dubbo", false, false,
                _ => Task.FromResult<IReadOnlyList<DubboServiceItem>>(
                    [new DubboServiceItem { Name = "new.Service" }]))
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(oldLoad.IsCompleted);
            Assert.Equal("new.Service", Assert.Single(newResult.Services).Name);
        }
        finally
        {
            oldResponse.SetResult([]);
            await oldLoad;
        }
    }

    [Fact]
    public async Task GetOrLoadAsync_ReusesAddressCacheAndReturnsIsolatedModels()
    {
        var cache = new DubboCatalogCache();
        int loadCount = 0;

        DubboCatalogLoadResult first = await cache.GetOrLoadAsync(
            "zk:2181",
            "/dubbo",
            requireAllServiceDetails: false,
            forceRefresh: false,
            _ =>
            {
                loadCount++;
                return Task.FromResult<IReadOnlyList<DubboServiceItem>>(
                    [new DubboServiceItem { Name = "demo.Service" }]);
            });
        first.Services[0].Methods.Add("page-local-mutation");

        DubboCatalogLoadResult second = await cache.GetOrLoadAsync(
            " zk:2181 ",
            "/dubbo",
            requireAllServiceDetails: false,
            forceRefresh: false,
            _ => throw new InvalidOperationException("不应再次读取"));

        Assert.Equal(1, loadCount);
        Assert.True(second.FromCache);
        Assert.Empty(second.Services[0].Methods);
    }

    [Fact]
    public async Task GetOrLoadAsync_UpgradesPartialCatalogWhenAllDetailsAreRequired()
    {
        var cache = new DubboCatalogCache();
        await cache.GetOrLoadAsync(
            "zk:2181",
            "/dubbo",
            requireAllServiceDetails: false,
            forceRefresh: false,
            _ => Task.FromResult<IReadOnlyList<DubboServiceItem>>(
                [new DubboServiceItem { Name = "demo.Service" }]));

        int fullLoadCount = 0;
        DubboCatalogLoadResult result = await cache.GetOrLoadAsync(
            "zk:2181",
            "/dubbo",
            requireAllServiceDetails: true,
            forceRefresh: false,
            cached =>
            {
                fullLoadCount++;
                Assert.NotNull(cached);
                return Task.FromResult<IReadOnlyList<DubboServiceItem>>(
                    [CreateLoadedService("demo.Service", "run")]);
            });

        Assert.Equal(1, fullLoadCount);
        Assert.False(result.FromCache);
        Assert.True(result.HasAllServiceDetails);
        Assert.True(result.PreloadAttempted);
        Assert.Equal(["run"], result.Services[0].Methods);
    }

    [Fact]
    public async Task GetOrLoadAsync_DoesNotRepeatAnIncompletePreloadWithoutForceRefresh()
    {
        var cache = new DubboCatalogCache();
        int preloadCount = 0;
        DubboCatalogLoadResult partial = await cache.GetOrLoadAsync(
            "zk:2181",
            "/dubbo",
            requireAllServiceDetails: true,
            forceRefresh: false,
            _ =>
            {
                preloadCount++;
                return Task.FromResult<IReadOnlyList<DubboServiceItem>>(
                    [new DubboServiceItem { Name = "temporarily-unavailable.Service" }]);
            });

        DubboCatalogLoadResult reused = await cache.GetOrLoadAsync(
            "zk:2181",
            "/dubbo",
            requireAllServiceDetails: true,
            forceRefresh: false,
            _ => throw new InvalidOperationException("部分预取结果不应自动触发整批重试"));

        Assert.Equal(1, preloadCount);
        Assert.True(partial.PreloadAttempted);
        Assert.False(partial.HasAllServiceDetails);
        Assert.True(reused.FromCache);
        Assert.Equal(1, reused.PendingServiceDetailCount);
    }

    [Fact]
    public async Task MarkPreloadAttempted_ReusesPartialBackgroundResultWithoutCallingLoader()
    {
        var cache = new DubboCatalogCache();
        await cache.GetOrLoadAsync(
            "zk:2181",
            "/dubbo",
            requireAllServiceDetails: false,
            forceRefresh: false,
            _ => Task.FromResult<IReadOnlyList<DubboServiceItem>>(
                [new DubboServiceItem { Name = "temporarily-unavailable.Service" }]));

        cache.MarkPreloadAttempted("zk:2181", "/dubbo");

        DubboCatalogLoadResult reused = await cache.GetOrLoadAsync(
            "zk:2181",
            "/dubbo",
            requireAllServiceDetails: true,
            forceRefresh: false,
            _ => throw new InvalidOperationException("后台预取结束后不应重新执行整批加载"));

        Assert.True(reused.FromCache);
        Assert.True(reused.PreloadAttempted);
        Assert.False(reused.HasAllServiceDetails);
    }

    [Fact]
    public async Task UpdateService_MakesLazilyLoadedDetailsAvailableToOtherPages()
    {
        var cache = new DubboCatalogCache();
        await cache.GetOrLoadAsync(
            "zk:2181",
            "/dubbo",
            requireAllServiceDetails: false,
            forceRefresh: false,
            _ => Task.FromResult<IReadOnlyList<DubboServiceItem>>(
                [new DubboServiceItem { Name = "demo.Service" }]));
        cache.UpdateService("zk:2181", "/dubbo", CreateLoadedService("demo.Service", "run"));

        var pageService = new DubboServiceItem { Name = "demo.Service" };
        bool applied = cache.TryApplyServiceDetails("zk:2181", "/dubbo", pageService);

        Assert.True(applied);
        Assert.True(pageService.IsLoaded);
        Assert.Equal(["run"], pageService.Methods);
    }

    [Fact]
    public async Task GetOrLoadAsync_ForceRefreshBypassesExistingEntry()
    {
        var cache = new DubboCatalogCache();
        int loadCount = 0;
        async Task<IReadOnlyList<DubboServiceItem>> Load(IReadOnlyList<DubboServiceItem>? _)
        {
            await Task.Yield();
            loadCount++;
            return [new DubboServiceItem { Name = $"Service{loadCount}" }];
        }

        await cache.GetOrLoadAsync("zk:2181", "/dubbo", false, false, Load);
        DubboCatalogLoadResult refreshed =
            await cache.GetOrLoadAsync("zk:2181", "/dubbo", false, true, Load);

        Assert.Equal(2, loadCount);
        Assert.False(refreshed.FromCache);
        Assert.Equal("Service2", refreshed.Services[0].Name);
    }

    [Fact]
    public async Task GetOrLoadAsync_KeepsSeparateCatalogsForSameAddress()
    {
        var cache = new DubboCatalogCache();
        await cache.GetOrLoadAsync(
            "zk:2181",
            "/dubbo-a",
            false,
            false,
            _ => Task.FromResult<IReadOnlyList<DubboServiceItem>>(
                [new DubboServiceItem { Name = "ServiceA", RegistryRootPath = "/dubbo-a" }]));
        await cache.GetOrLoadAsync(
            "zk:2181",
            "/dubbo-b",
            false,
            false,
            _ => Task.FromResult<IReadOnlyList<DubboServiceItem>>(
                [new DubboServiceItem { Name = "ServiceB", RegistryRootPath = "/dubbo-b" }]));

        DubboCatalogLoadResult first = await cache.GetOrLoadAsync(
            "zk:2181",
            "/dubbo-a",
            false,
            false,
            _ => throw new InvalidOperationException("第一个目录应继续命中缓存"));

        Assert.True(first.FromCache);
        Assert.Equal("ServiceA", Assert.Single(first.Services).Name);
    }

    [Fact]
    public async Task UpdateService_DistinguishesSameServiceNameAcrossRegistryRoots()
    {
        var cache = new DubboCatalogCache();
        await cache.GetOrLoadAsync(
            "zk:2181",
            "/dubbo-a\n/dubbo-b",
            false,
            false,
            _ => Task.FromResult<IReadOnlyList<DubboServiceItem>>(
            [
                new DubboServiceItem { Name = "demo.Service", RegistryRootPath = "/dubbo-a" },
                new DubboServiceItem { Name = "demo.Service", RegistryRootPath = "/dubbo-b" }
            ]));
        var loaded = new DubboServiceItem
        {
            Name = "demo.Service",
            RegistryRootPath = "/dubbo-b",
            IsLoaded = true
        };
        loaded.Methods.Add("fromB");

        cache.UpdateService("zk:2181", "/dubbo-a\n/dubbo-b", loaded);
        DubboCatalogLoadResult result = await cache.GetOrLoadAsync(
            "zk:2181",
            "/dubbo-a\n/dubbo-b",
            false,
            false,
            _ => throw new InvalidOperationException("应命中缓存"));

        Assert.False(result.Services.Single(service => service.RegistryRootPath == "/dubbo-a").IsLoaded);
        Assert.Equal(
            ["fromB"],
            result.Services.Single(service => service.RegistryRootPath == "/dubbo-b").Methods);
    }

    [Fact]
    public async Task GetOrLoadAsync_KeepsOnlyTenLeastRecentlyUsedAddresses()
    {
        var cache = new DubboCatalogCache();
        for (int index = 0; index < 10; index++)
        {
            int captured = index;
            await cache.GetOrLoadAsync(
                $"zk-{index}:2181",
                "/dubbo",
                false,
                false,
                _ => Task.FromResult<IReadOnlyList<DubboServiceItem>>(
                    [new DubboServiceItem { Name = $"Service{captured}" }]));
        }

        await cache.GetOrLoadAsync(
            "zk-0:2181",
            "/dubbo",
            false,
            false,
            _ => throw new InvalidOperationException("最近使用的地址应仍在缓存中"));
        await cache.GetOrLoadAsync(
            "zk-10:2181",
            "/dubbo",
            false,
            false,
            _ => Task.FromResult<IReadOnlyList<DubboServiceItem>>(
                [new DubboServiceItem { Name = "Service10" }]));

        int reloadCount = 0;
        await cache.GetOrLoadAsync(
            "zk-1:2181",
            "/dubbo",
            false,
            false,
            _ =>
            {
                reloadCount++;
                return Task.FromResult<IReadOnlyList<DubboServiceItem>>(
                    [new DubboServiceItem { Name = "Reloaded" }]);
            });

        Assert.Equal(1, reloadCount);
    }

    private static DubboServiceItem CreateLoadedService(string name, string method)
    {
        var service = new DubboServiceItem { Name = name, IsLoaded = true };
        service.Methods.Add(method);
        return service;
    }
}
