using Microsoft.AspNetCore.Components.WebView;

namespace DubboTester.Desktop;

public partial class MainPage : ContentPage
{
	private static readonly HashSet<string> InternalBlazorHosts = new(StringComparer.OrdinalIgnoreCase)
	{
		"0.0.0.0",
		"0.0.0.1"
	};

	public MainPage()
	{
		InitializeComponent();
	}

	private void OnUrlLoading(object? sender, UrlLoadingEventArgs args)
	{
		if (args.Url.Scheme is "http" or "https"
			&& !InternalBlazorHosts.Contains(args.Url.Host))
		{
			args.UrlLoadingStrategy = UrlLoadingStrategy.OpenExternally;
			return;
		}

		// BlazorWebView uses a synthetic 0.0.0.x HTTPS origin for its bundled
		// content. Windows currently uses 0.0.0.1 while other platforms can use
		// 0.0.0.0. These navigations must stay inside the desktop WebView.
		args.UrlLoadingStrategy = UrlLoadingStrategy.OpenInWebView;
	}
}
