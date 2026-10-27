using System.Net;
using System.Net.Sockets;
using DubboTester.Services;

namespace DubboTester.Desktop;

public partial class App : Application
{
	private readonly IDesktopMcpServer _mcpServer;
	private readonly LocalGatewayListenerStatus _listenerStatus;

	public App(IDesktopMcpServer mcpServer, LocalGatewayListenerStatus listenerStatus)
	{
		_mcpServer = mcpServer;
		_listenerStatus = listenerStatus;
		InitializeComponent();
		_ = StartMcpServerAsync();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(new MainPage())
		{
			Title = "Dubbo Tester",
			MinimumWidth = 1024,
			MinimumHeight = 700,
			Width = 1440,
			Height = 900
		};
		window.Destroying += OnWindowDestroying;
		return window;
	}

	private async Task StartMcpServerAsync()
	{
		try
		{
			Console.Error.WriteLine("Starting desktop MCP endpoint...");
			await _mcpServer.StartAsync();
			Console.Error.WriteLine("Desktop MCP endpoint started.");
		}
		catch (Exception exception)
		{
			_listenerStatus.ReportFailure(DesktopMcpEndpoint.Port, IsAddressInUse(exception));
			System.Diagnostics.Debug.WriteLine($"MCP endpoint startup failed: {exception}");
			Console.Error.WriteLine($"MCP endpoint startup failed: {exception}");
		}
	}

	private static bool IsAddressInUse(Exception exception)
	{
		for (Exception? current = exception; current is not null; current = current.InnerException)
		{
			if (current is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }
				or HttpListenerException { ErrorCode: 48 or 98 or 10048 })
				return true;
			if (current.GetType().Name == "AddressInUseException")
				return true;
		}
		return false;
	}

	private async void OnWindowDestroying(object? sender, EventArgs args)
	{
		await _mcpServer.DisposeAsync();
	}
}
