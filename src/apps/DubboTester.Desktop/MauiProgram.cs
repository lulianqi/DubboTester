using DubboTester.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DubboTester.Desktop;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

		builder.Services.AddMauiBlazorWebView();
		builder.Services.AddSingleton<GlobalRuntimeSettings>();
		builder.Services.AddSingleton(new ClientEnvironment(isDesktop: true));
		builder.Services.AddSingleton<IClipboardService, DesktopClipboardService>();
		builder.Services.AddSingleton<LocalGatewayListenerStatus>();
		builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
		{
			["DubboTester:Version"] = "2.1.1"
		});

		builder.Services.AddSingleton<IAppDataDirectoryProvider, DesktopAppDataDirectoryProvider>();
		builder.Services.AddSingleton<DesktopMcpRequestContextAccessor>();
		builder.Services.AddSingleton<IMcpRequestContextAccessor>(services =>
			services.GetRequiredService<DesktopMcpRequestContextAccessor>());
		builder.Services.AddSingleton<IMcpEndpointAddressProvider, DesktopMcpEndpointAddressProvider>();
		builder.Services.AddSingleton<DubboCatalogCache>();
		builder.Services.AddSingleton<DubboCatalogPreloadService>();
		builder.Services.AddSingleton<McpGatewayService>();
		builder.Services.AddSingleton<HttpGatewayService>();
		builder.Services.AddScoped<DubboSession>();
		builder.Services.AddScoped<DubboPressureTestRunner>();
		builder.Services.AddScoped<PressureTestDraftStore>();
		builder.Services.AddSingleton<IPressureTestReportExportService, DesktopPressureTestReportExportService>();
#if WINDOWS
		builder.Services.AddSingleton<IDesktopMcpServer, WindowsDesktopMcpServer>();
#else
		builder.Services.AddSingleton<IDesktopMcpServer, DesktopMcpHttpServer>();
#endif

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
