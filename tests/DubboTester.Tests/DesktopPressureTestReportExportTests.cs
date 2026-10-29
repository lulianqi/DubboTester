using System.Text;
using System.Xml;
using System.Xml.Linq;
using DubboTester.Desktop;
using DubboTester.Models;
using DubboTester.Services;
using Xunit;

namespace DubboTester.Tests;

public sealed class DesktopPressureTestReportExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"dubbo-export-test-{Guid.NewGuid():N}");

    [Fact]
    public async Task SavesCompleteUtf8ReportToDownloads()
    {
        string downloads = CreateDirectory("Downloads");
        string exports = Path.Combine(_root, "Exports");
        var exporter = new DesktopPressureTestReportExportService(downloads, exports);

        PressureTestReportExportResult result = await exporter.ExportAsync(CreateRequest());

        Assert.NotNull(result.SavedPath);
        Assert.Equal(Path.Combine(downloads, "report.html"), result.SavedPath);
        Assert.Equal("report.html", result.FileName);
        Assert.Null(result.DownloadUrl);
        string html = await File.ReadAllTextAsync(result.SavedPath, Encoding.UTF8);
        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("中文压测方法", html);
        Assert.Empty(Directory.EnumerateFiles(exports));
    }

    [Fact]
    public async Task PreservesExistingReportAndReturnsTheActualRenamedFileName()
    {
        string downloads = CreateDirectory("Downloads");
        string original = Path.Combine(downloads, "report.html");
        await File.WriteAllTextAsync(original, "existing report");
        var exporter = new DesktopPressureTestReportExportService(downloads, Path.Combine(_root, "Exports"));

        PressureTestReportExportResult result = await exporter.ExportAsync(CreateRequest());

        Assert.Equal("existing report", await File.ReadAllTextAsync(original));
        Assert.Equal("report-2.html", result.FileName);
        Assert.Equal(Path.Combine(downloads, "report-2.html"), result.SavedPath);
        Assert.True(File.Exists(result.SavedPath));
    }

    [Fact]
    public async Task MissingDownloadsFallsBackToApplicationExports()
    {
        string exports = Path.Combine(_root, "Exports");
        var exporter = new DesktopPressureTestReportExportService(Path.Combine(_root, "missing"), exports);

        PressureTestReportExportResult result = await exporter.ExportAsync(CreateRequest());

        Assert.NotNull(result.SavedPath);
        Assert.Equal(Path.Combine(exports, "report.html"), result.SavedPath);
        Assert.StartsWith("<!doctype html>", await File.ReadAllTextAsync(result.SavedPath));
        Assert.Single(Directory.EnumerateFiles(exports));
    }

    [Fact]
    public async Task UnixDeniedDownloadsKeepsCompleteReportInApplicationExports()
    {
        if (OperatingSystem.IsWindows()) return;

        string downloads = CreateDirectory("Downloads");
        string exports = Path.Combine(_root, "Exports");
        File.SetUnixFileMode(downloads, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var exporter = new DesktopPressureTestReportExportService(downloads, exports);

            PressureTestReportExportResult result = await exporter.ExportAsync(CreateRequest());

            Assert.NotNull(result.SavedPath);
            Assert.Equal(Path.Combine(exports, "report.html"), result.SavedPath);
            Assert.StartsWith("<!doctype html>", await File.ReadAllTextAsync(result.SavedPath));
            Assert.Empty(Directory.EnumerateFiles(downloads));
            Assert.Single(Directory.EnumerateFiles(exports));
        }
        finally
        {
            File.SetUnixFileMode(downloads,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task CancelledExportDoesNotCreateFiles()
    {
        var exporter = new DesktopPressureTestReportExportService(
            Path.Combine(_root, "Downloads"), Path.Combine(_root, "Exports"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            exporter.ExportAsync(CreateRequest(), cancellation.Token));

        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task GenerationFailureCleansTemporaryFileAndPreservesOriginalError()
    {
        string exports = Path.Combine(_root, "Exports");
        var exporter = new DesktopPressureTestReportExportService(Path.Combine(_root, "missing"), exports);
        PressureTestReportExportRequest request = CreateRequest() with { Configuration = null! };

        ArgumentNullException error = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            exporter.ExportAsync(request));

        Assert.Equal("configuration", error.ParamName);
        Assert.Empty(Directory.EnumerateFiles(exports));
    }

    [Fact]
    public void MacSandboxIncludesDownloadsWriteEntitlement()
    {
        using Stream stream = typeof(DesktopPressureTestReportExportTests).Assembly
            .GetManifestResourceStream("DesktopLayout.Entitlements.plist")!;
        using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null
        });
        XDocument manifest = XDocument.Load(reader);
        foreach (string entitlement in new[]
        {
            "com.apple.security.app-sandbox",
            "com.apple.security.files.downloads.read-write"
        })
        {
            XElement key = Assert.Single(manifest.Descendants("key"), item => item.Value == entitlement);
            Assert.Equal("true", key.ElementsAfterSelf().First().Name.LocalName);
        }
    }

    private string CreateDirectory(string name)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static PressureTestReportExportRequest CreateRequest() => new(
        new PressureTestConfiguration
        {
            Settings = new TesterSettings(),
            Invocation = new InvocationInput { ServiceName = "com.foo.Service", MethodName = "中文压测方法" }
        },
        new PressureTestSnapshot { TotalRequests = 1, CompletedRequests = 1, SuccessCount = 1 },
        DateTimeOffset.Now,
        DateTimeOffset.Now,
        IncludeAllRequests: true,
        ThroughputSmoothingSeconds: 5,
        UseRecentLatency: false,
        FileName: "report.html");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
