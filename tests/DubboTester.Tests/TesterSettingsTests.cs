using DubboTester.Models;
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
}
