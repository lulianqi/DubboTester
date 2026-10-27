using DubboNet.Clients;
using DubboNet.Clients.DataModle;

namespace DubboTester.Services;

// Deliberately separate from persisted connection and gateway settings.
public sealed class GlobalRuntimeSettings
{
    public const string ConsumerApplicationName = "DubboTester";
    private readonly object _sync = new();
    private readonly Action<ConsumerRegistrationOptions> _configure;
    private bool _consumerRegistrationEnabled;

    public GlobalRuntimeSettings() : this(DubboClient.ConfigureConsumerRegistration) { }

    internal GlobalRuntimeSettings(Action<ConsumerRegistrationOptions> configure)
    {
        _configure = configure;
        _configure(CreateOptions(enabled: false));
    }

    public bool ConsumerRegistrationEnabled
    {
        get { lock (_sync) return _consumerRegistrationEnabled; }
    }

    public void SetConsumerRegistration(bool enabled)
    {
        lock (_sync)
        {
            if (_consumerRegistrationEnabled == enabled) return;
            _configure(CreateOptions(enabled));
            _consumerRegistrationEnabled = enabled;
        }
    }

    private static ConsumerRegistrationOptions CreateOptions(bool enabled) => new()
    {
        Enabled = enabled,
        ApplicationName = ConsumerApplicationName
    };
}
