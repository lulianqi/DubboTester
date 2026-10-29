using System.Text;
using DubboTester.Services;

namespace DubboTester.Desktop;

internal sealed class DesktopPressureTestReportExportService : IPressureTestReportExportService
{
    private readonly string _downloadsDirectory;
    private readonly string _fallbackDirectory;

    public DesktopPressureTestReportExportService(IAppDataDirectoryProvider appDataDirectoryProvider)
        : this(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            Path.Combine(appDataDirectoryProvider.DirectoryPath, "Exports"))
    {
    }

    internal DesktopPressureTestReportExportService(string downloadsDirectory, string fallbackDirectory)
    {
        _downloadsDirectory = downloadsDirectory;
        _fallbackDirectory = fallbackDirectory;
    }

    public async Task<PressureTestReportExportResult> ExportAsync(
        PressureTestReportExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // Generate inside the application sandbox first. Merely checking whether
        // Downloads exists does not mean a sandboxed app has permission to write it.
        Directory.CreateDirectory(_fallbackDirectory);
        string fileName = Path.GetFileName(request.FileName);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = $"dubbo-pressure-{DateTime.Now:yyyyMMdd-HHmmss}.html";
        string filePath = GetAvailableFilePath(_fallbackDirectory, fileName);
        string temporaryPath = Path.Combine(_fallbackDirectory, $"{Guid.NewGuid():N}.partial");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024))
                {
                    await PressureTestHtmlReportBuilder.WriteAsync(
                        writer,
                        request.Configuration,
                        request.Snapshot,
                        request.StartedAt,
                        request.FinishedAt,
                        request.IncludeAllRequests,
                        request.ThroughputSmoothingSeconds,
                        request.UseRecentLatency,
                        cancellationToken).ConfigureAwait(false);
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, filePath);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }

        if (Directory.Exists(_downloadsDirectory))
        {
            try
            {
                string downloadsPath = GetAvailableFilePath(_downloadsDirectory, fileName);
                File.Move(filePath, downloadsPath);
                filePath = downloadsPath;
            }
            catch (Exception exception) when (
                (exception is IOException or UnauthorizedAccessException) && File.Exists(filePath))
            {
                // The complete report remains in Exports if Downloads is denied,
                // unavailable or full. The UI displays the actual saved path.
            }
        }

        return new PressureTestReportExportResult(Path.GetFileName(filePath), SavedPath: filePath);
    }

    private static string GetAvailableFilePath(string directoryPath, string fileName)
    {
        string candidate = Path.Combine(directoryPath, fileName);
        if (!File.Exists(candidate)) return candidate;

        string baseName = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        for (int suffix = 2; ; suffix++)
        {
            candidate = Path.Combine(directoryPath, $"{baseName}-{suffix}{extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static void TryDelete(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cleanup must not hide the original generation/cancellation error.
        }
    }
}
