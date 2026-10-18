using DubboTester.Components;
using DubboTester.Components.Pages;
using DubboTester.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("version.json", optional: false, reloadOnChange: false);

builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddDubboTesterWebServices();
builder.Services.AddDubboTesterMcpServer();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();
app.MapDubboTesterMcpServer();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(Home).Assembly);

app.Run();
