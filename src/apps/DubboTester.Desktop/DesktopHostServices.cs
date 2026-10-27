using DubboTester.Services;

namespace DubboTester.Desktop;

internal sealed class DesktopAppDataDirectoryProvider : IAppDataDirectoryProvider
{
    public string DirectoryPath { get; } = Path.Combine(
        FileSystem.AppDataDirectory,
        "App_Data");
}

internal sealed class DesktopMcpRequestContextAccessor : IMcpRequestContextAccessor
{
    private readonly AsyncLocal<string?> _remoteAddress = new();

    public string GetRemoteAddress() => _remoteAddress.Value ?? string.Empty;

    public IDisposable Push(string? remoteAddress)
    {
        string? previous = _remoteAddress.Value;
        _remoteAddress.Value = remoteAddress;
        return new CallbackDisposable(() => _remoteAddress.Value = previous);
    }

    private sealed class CallbackDisposable(Action callback) : IDisposable
    {
        public void Dispose() => callback();
    }
}

internal sealed class DesktopMcpEndpointAddressProvider : IMcpEndpointAddressProvider
{
    public string EndpointOverride => DesktopMcpEndpoint.Address;
}


internal static class DesktopMcpEndpoint
{
    public const int Port = 5154;
    public const string Address = "http://localhost:5154/mcp";
    public static readonly string[] ListenerAddresses =
    [
        "http://127.0.0.1:5154"
    ];
    public static readonly string[] ListenerPrefixes =
    [
        "http://127.0.0.1:5154/",
        "http://localhost:5154/"
    ];
}


public interface IDesktopMcpServer : IAsyncDisposable
{
    Task StartAsync();
}
