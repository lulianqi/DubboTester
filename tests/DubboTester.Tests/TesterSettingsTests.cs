using DubboTester.Models;
using DubboTester.Services;
using System.Text.Json;
using Xunit;

namespace DubboTester.Tests;

public sealed class TesterSettingsTests
{
    [Fact]
    public void Defaults_UseTenSecondRequestAndMetadataFallbackTimeouts()
    {
        var settings = new TesterSettings();

        Assert.Equal(10_000, settings.RequestTimeoutMs);
        Assert.Equal(10_000, settings.MetadataFallbackTimeoutMs);
    }

    [Fact]
    public void Defaults_UseAutomaticRegistryDiscoveryAndFiveThousandHistoryEntries()
    {
        var settings = new TesterSettings();

        Assert.Equal("/", settings.RegistryRootPath);
        Assert.Equal(5_000, settings.HistoryLimit);
        Assert.True(settings.SaveResponseInHistory);
    }

    [Fact]
    public void NacosSettings_BuildConnectionAndMetadataFollowsSelectedGroup()
    {
        var settings = new TesterSettings
        {
            RegistryType = RegistryCenterType.Nacos,
            RegistryAddress = "nacos.example:8848?namespace=prod"
        };

        Assert.Equal(
            "nacos://nacos.example:8848?namespace=prod",
            settings.RegistryConnectionString);
        Assert.Equal(
            "DEFAULT_GROUP",
            RegistryConnectionSettings.ResolveMetadataRoot(settings, "/DEFAULT_GROUP", null));
        Assert.Equal(
            "blue",
            RegistryConnectionSettings.ResolveMetadataRoot(settings, "/blue", null));
        settings.MetadataRootPath = "/";
        Assert.Equal(
            "blue",
            RegistryConnectionSettings.ResolveMetadataRoot(settings, "/blue", null));
        settings.MetadataRootPath = "dubbo-metadata";
        Assert.Equal(
            "dubbo-metadata",
            RegistryConnectionSettings.ResolveMetadataRoot(settings, "/blue", null));
        Assert.Equal("DEFAULT_GROUP", RegistryConnectionSettings.NacosGroup("/"));
        Assert.Equal("blue", RegistryConnectionSettings.NacosGroup("/blue"));
    }

    [Fact]
    public void ZooKeeperSettings_RejectNacosConnectionPrefix()
    {
        var settings = new TesterSettings
        {
            RegistryType = RegistryCenterType.ZooKeeper,
            RegistryAddress = "nacos://127.0.0.1:8848"
        };

        Assert.Throws<ArgumentException>(() => settings.RegistryConnectionString);
    }

    [Fact]
    public void LegacySettings_KeepZooKeeperAddressAndDefaultRegistryType()
    {
        TesterSettings? settings = JsonSerializer.Deserialize<TesterSettings>(
            """{"ZooKeeperAddress":"zk.example:2181","RegistryRootPath":"/dubbo"}""");

        Assert.NotNull(settings);
        Assert.Equal(RegistryCenterType.ZooKeeper, settings.RegistryType);
        Assert.Equal("zk.example:2181", settings.RegistryAddress);
    }
}
