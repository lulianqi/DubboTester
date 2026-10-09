using DubboTester.Services;
using Xunit;

namespace DubboTester.Tests;

public sealed class RegistryRootPathsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("/")]
    public void Parse_EmptyOrSlashEnablesAutoDiscovery(string? value)
    {
        RegistryRootSelection selection = RegistryRootPaths.Parse(value);

        Assert.True(selection.AutoDiscover);
        Assert.Empty(selection.ExplicitRoots);
        Assert.Equal("/", selection.CacheKey);
    }

    [Fact]
    public void Parse_SingleRootKeepsLegacyCacheKey()
    {
        RegistryRootSelection selection = RegistryRootPaths.Parse("dubbo/");

        Assert.False(selection.AutoDiscover);
        Assert.Equal(["/dubbo"], selection.ExplicitRoots);
        Assert.Equal("/dubbo", selection.CacheKey);
    }

    [Fact]
    public void Parse_MultipleRootsNormalizesDeduplicatesAndUsesStableCacheKey()
    {
        RegistryRootSelection first = RegistryRootPaths.Parse("/dubbo-b, dubbo-a; /dubbo-b");
        RegistryRootSelection second = RegistryRootPaths.Parse("/dubbo-a，/dubbo-b");

        Assert.Equal(["/dubbo-a", "/dubbo-b"], first.ExplicitRoots);
        Assert.Equal(first.CacheKey, second.CacheKey);
    }

    [Fact]
    public void Parse_SlashCanBeCombinedWithExplicitRoots()
    {
        RegistryRootSelection selection = RegistryRootPaths.Parse("/, /custom");

        Assert.True(selection.AutoDiscover);
        Assert.Equal(["/custom"], selection.ExplicitRoots);
        Assert.Equal("/\n/custom", selection.CacheKey);
    }

    [Fact]
    public void ResolveInvocationRoot_UsesSelectedServiceRootBeforeConfiguredRoots()
    {
        Assert.Equal(
            "/selected",
            RegistryRootPaths.ResolveInvocationRoot("/a,/b", "/selected"));
        Assert.Equal(
            "/a",
            RegistryRootPaths.ResolveInvocationRoot("/b,/a", null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ResolveMetadataRoot_EmptyValueFollowsSelectedRegistryRoot(string? metadataRoot)
    {
        Assert.Equal(
            "/dubbo-3.1.11",
            RegistryRootPaths.ResolveMetadataRoot(metadataRoot, "/dubbo-3.1.11"));
    }

    [Fact]
    public void ResolveMetadataRoot_ExplicitValueOverridesRegistryRoot()
    {
        Assert.Equal(
            "/metadata-center",
            RegistryRootPaths.ResolveMetadataRoot("metadata-center/", "/dubbo-3.1.11"));
    }
}
