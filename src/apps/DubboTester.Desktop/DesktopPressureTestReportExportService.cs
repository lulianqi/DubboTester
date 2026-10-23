using System.Text;
using DubboTester.Services;

namespace DubboTester.Desktop;

internal sealed class DesktopPressureTestReportExportService : IPressureTestReportExportService
{
    public async Task<PressureTestReportExportResult> ExportAsync(
        PressureTestReportExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string directoryPath = GetExportDirectoryPath();
        Directory.CreateDirectory(directoryPath);
        string fileName = Path.GetFileName(request.FileName);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = $"dubbo-pressure-{DateTime.Now:yyyyMMdd-HHmmss}.html";
        string filePath = GetAvailableFilePath(directoryPath, fileName);
        string temporaryPath = $"{filePath}.{Guid.NewGuid():N}.partial";

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
            File.Move(temporaryPath, filePath);
        }
        catch
        {
            File.Delete(temporaryPath);
            throw;
        }

        return new PressureTestReportExportResult(fileName, SavedPath: filePath);
    }

    private static string GetExportDirectoryPath()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string downloadsPath = Path.Combine(userProfile, "Downloads");
        return Directory.Exists(downloadsPath)
            ? downloadsPath
            : Path.Combine(FileSystem.AppDataDirectory, "Exports");
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
}
