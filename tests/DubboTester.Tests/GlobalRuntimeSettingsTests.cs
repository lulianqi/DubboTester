using DubboNet.Clients.DataModle;
using DubboTester.Services;
using Xunit;

namespace DubboTester.Tests;

public sealed class GlobalRuntimeSettingsTests
{
    [Fact]
    public void NewRuntime_StartsWithConsumerRegistrationDisabled()
    {
        var configured = new List<ConsumerRegistrationOptions>();
        var settings = new GlobalRuntimeSettings(configured.Add);

        Assert.False(settings.ConsumerRegistrationEnabled);
        ConsumerRegistrationOptions options = Assert.Single(configured);
        Assert.False(options.Enabled);
        Assert.Equal("DubboTester", options.ApplicationName);
    }

    [Fact]
    public void Toggle_UpdatesProcessConfigurationWithoutDuplicateCalls()
    {
        var configured = new List<ConsumerRegistrationOptions>();
        var settings = new GlobalRuntimeSettings(configured.Add);

        settings.SetConsumerRegistration(true);
        Assert.True(settings.ConsumerRegistrationEnabled);
        settings.SetConsumerRegistration(true);
        settings.SetConsumerRegistration(false);
        settings.SetConsumerRegistration(false);

        Assert.False(settings.ConsumerRegistrationEnabled);
        Assert.Equal(new[] { false, true, false }, configured.Select(options => options.Enabled));
        Assert.All(configured, options => Assert.Equal("DubboTester", options.ApplicationName));
    }

    [Fact]
    public void NewRuntime_DoesNotRestorePreviouslyEnabledState()
    {
        var first = new GlobalRuntimeSettings(_ => { });
        first.SetConsumerRegistration(true);

        var restarted = new GlobalRuntimeSettings(_ => { });

        Assert.True(first.ConsumerRegistrationEnabled);
        Assert.False(restarted.ConsumerRegistrationEnabled);
    }

    [Fact]
    public void FailedConfiguration_DoesNotChangeDisplayedState()
    {
        var settings = new GlobalRuntimeSettings(options =>
        {
            if (options.Enabled) throw new InvalidOperationException("Configuration rejected");
        });

        Assert.Throws<InvalidOperationException>(() => settings.SetConsumerRegistration(true));
        Assert.False(settings.ConsumerRegistrationEnabled);
    }
}
