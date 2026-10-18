using Microsoft.AspNetCore.Components.WebView;

namespace DubboTester.Desktop;

public partial class MainPage : ContentPage
{
	public MainPage()
	{
		InitializeComponent();
	}

	private void OnUrlLoading(object? sender, UrlLoadingEventArgs args)
	{
		if (args.Url.Scheme is "http" or "https"
			&& !string.Equals(args.Url.Host, "0.0.0.0", StringComparison.OrdinalIgnoreCase))
		{
			args.UrlLoadingStrategy = UrlLoadingStrategy.OpenExternally;
		}
	}
}
