#if WINDOWS
using DubboTester.Hosting;
using DubboTester.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DubboTester.Desktop;

/// <summary>
/// Uses Kestrel on Windows so an unpackaged desktop application can bind its
/// loopback MCP endpoint without requiring an HTTP.sys URL ACL reservation.
/// </summary>
internal sealed class WindowsDesktopMcpServer : IDesktopMcpServer
{
    private readonly McpGatewayService _gateway;
    private readonly HttpGatewayService _httpGateway;
    private readonly DesktopMcpRequestContextAccessor _requestContextAccessor;
    private readonly ILogger<WindowsDesktopMcpServer> _logger;
    private WebApplication? _application;

    public WindowsDesktopMcpServer(
        McpGatewayService gateway,
        HttpGatewayService httpGateway,
        DesktopMcpRequestContextAccessor requestContextAccessor,
        ILogger<WindowsDesktopMcpServer> logger)
    {
        _gateway = gateway;
        _httpGateway = httpGateway;
        _requestContextAccessor = requestContextAccessor;
        _logger = logger;
    }

    public async Task StartAsync()
    {
        if (_application is not null) return;

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(WindowsDesktopMcpServer).Assembly.FullName
        });
        builder.WebHost.UseUrls(DesktopMcpEndpoint.ListenerAddresses);
        builder.Services.AddSingleton(_gateway);
        builder.Services.AddSingleton(_httpGateway);
        builder.Services.AddDubboTesterMcpServer();

        WebApplication application = builder.Build();
        application.Use(async (context, next) =>
        {
            using IDisposable requestAddress = _requestContextAccessor.Push(
                context.Connection.RemoteIpAddress?.ToString());
            await next();
        });
        application.MapDubboTesterMcpServer();
        application.MapDubboTesterHttpGateway();

        try
        {
            await application.StartAsync();
            _application = application;
            _logger.LogInformation(
                "Desktop MCP endpoint started at {Endpoint}",
                DesktopMcpEndpoint.Address);
        }
        catch
        {
            await application.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_application is null) return;

        WebApplication application = _application;
        _application = null;
        await application.StopAsync();
        await application.DisposeAsync();
    }
}
#endif
