using DubboTester.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DubboTester.Hosting;

public static class DubboTesterServiceCollectionExtensions
{
    public static IServiceCollection AddDubboTesterApplication(
        this IServiceCollection services)
    {
        services.AddSingleton<DubboCatalogCache>();
        services.AddSingleton<DubboCatalogPreloadService>();
        services.AddSingleton<McpGatewayService>();
        services.AddSingleton<HttpGatewayService>();
        services.AddScoped<DubboSession>();
        services.AddScoped<DubboPressureTestRunner>();
        services.AddScoped<PressureTestDraftStore>();
        return services;
    }

    public static IServiceCollection AddDubboTesterWebServices(
        this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.TryAddSingleton<IAppDataDirectoryProvider, WebAppDataDirectoryProvider>();
        services.TryAddSingleton<IMcpRequestContextAccessor, AspNetMcpRequestContextAccessor>();
        services.TryAddSingleton<IMcpEndpointAddressProvider, WebMcpEndpointAddressProvider>();
        services.TryAddSingleton<PressureTestReportDownloadStore>();
        services.TryAddSingleton<IPressureTestReportExportService, WebPressureTestReportExportService>();
        return services.AddDubboTesterApplication();
    }

    private sealed class WebAppDataDirectoryProvider(IWebHostEnvironment environment)
        : IAppDataDirectoryProvider
    {
        public string DirectoryPath { get; } = Path.Combine(
            environment.ContentRootPath,
            "App_Data");
    }

    private sealed class AspNetMcpRequestContextAccessor(IHttpContextAccessor accessor)
        : IMcpRequestContextAccessor
    {
        public string GetRemoteAddress() =>
            accessor.HttpContext?.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
    }

    private sealed class WebMcpEndpointAddressProvider : IMcpEndpointAddressProvider
    {
        public string? EndpointOverride => null;
    }
}
