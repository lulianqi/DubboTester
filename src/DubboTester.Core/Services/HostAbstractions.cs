namespace DubboTester.Services;

/// <summary>
/// Resolves the writable directory used for application-owned data.
/// Each host supplies a platform-appropriate implementation.
/// </summary>
public interface IAppDataDirectoryProvider
{
    string DirectoryPath { get; }
}

/// <summary>
/// Supplies transport information for MCP audit records without coupling the
/// application layer to ASP.NET Core.
/// </summary>
public interface IMcpRequestContextAccessor
{
    string GetRemoteAddress();
}

/// <summary>
/// Allows a host to override the MCP endpoint shown in the shared UI.
/// Web hosts normally use the current request origin; desktop uses its
/// loopback listener address.
/// </summary>
public interface IMcpEndpointAddressProvider
{
    string? EndpointOverride { get; }
}
