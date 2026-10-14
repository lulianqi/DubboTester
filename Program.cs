using DubboTester.Components;
using DubboTester.Services;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSingleton<DubboCatalogCache>();
builder.Services.AddSingleton<DubboCatalogPreloadService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<McpGatewayService>();
builder.Services.AddScoped<DubboSession>();
builder.Services.AddScoped<DubboPressureTestRunner>();
builder.Services.AddScoped<PressureTestDraftStore>();
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInstructions =
            "仅提供在 Dubbo Tester 的 MCP 管理页面中明确启用的 Dubbo 方法。";
    })
    .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
    .WithListToolsHandler((context, _) =>
    {
        McpGatewayService gateway = context.Services!.GetRequiredService<McpGatewayService>();
        return ValueTask.FromResult(new ListToolsResult
        {
            Tools = [.. gateway.GetTools()],
            TimeToLive = TimeSpan.Zero,
            CacheScope = CacheScope.Private
        });
    })
    .WithCallToolHandler((context, cancellationToken) =>
    {
        McpGatewayService gateway = context.Services!.GetRequiredService<McpGatewayService>();
        return new ValueTask<CallToolResult>(gateway.CallToolAsync(context.Params!, cancellationToken));
    });

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/mcp"))
    {
        McpGatewayService gateway = context.RequestServices.GetRequiredService<McpGatewayService>();
        if (!gateway.IsEnabled)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new { error = "MCP 服务未启用。" });
            return;
        }

        string? explicitToken = context.Request.Headers["X-MCP-Token"].FirstOrDefault();
        if (gateway.IsAuthenticationEnabled
            && !gateway.IsAuthorized(context.Request.Headers.Authorization, explicitToken))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await context.Response.WriteAsJsonAsync(new { error = "MCP 访问令牌无效。" });
            return;
        }
    }

    await next();
});

app.MapStaticAssets();
app.MapMcp("/mcp");
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
