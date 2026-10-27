using Microsoft.JSInterop;

namespace DubboTester.Services;

public sealed class BrowserClipboardService(IJSRuntime js) : IClipboardService
{
    public Task SetTextAsync(string text) => js.InvokeVoidAsync("dubboTester.copy", text).AsTask();
}
