namespace DubboTester.Desktop;

public partial class App : Application
{
	private readonly IDesktopMcpServer _mcpServer;

	public App(IDesktopMcpServer mcpServer)
	{
		_mcpServer = mcpServer;
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
			System.Diagnostics.Debug.WriteLine($"MCP endpoint startup failed: {exception}");
			Console.Error.WriteLine($"MCP endpoint startup failed: {exception}");
		}
	}

	private async void OnWindowDestroying(object? sender, EventArgs args)
	{
		await _mcpServer.DisposeAsync();
	}
}
