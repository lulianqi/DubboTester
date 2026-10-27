using DubboTester.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.DataTransfer;

namespace DubboTester.Desktop;

public sealed class DesktopClipboardService : IClipboardService
{
    public Task SetTextAsync(string text) =>
        MainThread.InvokeOnMainThreadAsync(() => Clipboard.Default.SetTextAsync(text));
}
