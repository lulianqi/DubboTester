using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using DubboTester.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace DubboTester.Hosting;

internal sealed class WebPressureTestReportExportService(PressureTestReportDownloadStore store)
    : IPressureTestReportExportService
{
    public Task<PressureTestReportExportResult> ExportAsync(
        PressureTestReportExportRequest request,
        CancellationToken cancellationToken = default) =>
        store.CreateAsync(request, cancellationToken);
}

internal sealed class PressureTestReportDownloadStore
{
    private static readonly TimeSpan ReportLifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, ReportDownload> _downloads = new(StringComparer.Ordinal);
    private readonly string _directoryPath;

    public PressureTestReportDownloadStore(IAppDataDirectoryProvider appDataDirectoryProvider)
    {
        _directoryPath = Path.Combine(appDataDirectoryProvider.DirectoryPath, "PressureTestReports");
        Directory.CreateDirectory(_directoryPath);
        DeleteOrphanedFiles();
    }

    public async Task<PressureTestReportExportResult> CreateAsync(
        PressureTestReportExportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        CleanupExpiredDownloads();

        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        string fileName = Path.GetFileName(request.FileName);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = $"dubbo-pressure-{DateTime.Now:yyyyMMdd-HHmmss}.html";
        string filePath = Path.Combine(_directoryPath, $"{token}.html");

        try
        {
            await using var stream = new FileStream(
                filePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024);
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
        catch
        {
            TryDelete(filePath);
            throw;
        }

        _downloads[token] = new ReportDownload(
            filePath,
            fileName,
            DateTimeOffset.UtcNow.Add(ReportLifetime));
        return new PressureTestReportExportResult(
            fileName,
            DownloadUrl: $"/api/pressure-test/reports/{token}");
    }

    public bool TryTake(string token, out ReportDownload download)
    {
        download = null!;
        CleanupExpiredDownloads();
        if (!IsValidToken(token) || !_downloads.TryRemove(token, out ReportDownload? stored)) return false;
        download = stored;
        if (download.ExpiresAt <= DateTimeOffset.UtcNow || !File.Exists(download.FilePath))
        {
            TryDelete(download.FilePath);
            download = null!;
            return false;
        }
        return true;
    }

    public void Delete(ReportDownload download) => TryDelete(download.FilePath);

    private void CleanupExpiredDownloads()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach ((string token, ReportDownload download) in _downloads)
        {
            if (download.ExpiresAt > now || !_downloads.TryRemove(token, out ReportDownload? removed)) continue;
            TryDelete(removed.FilePath);
        }
    }

    private void DeleteOrphanedFiles()
    {
        foreach (string filePath in Directory.EnumerateFiles(_directoryPath, "*.html"))
        {
            try
            {
                File.Delete(filePath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static bool IsValidToken(string token) =>
        token.Length == 64 && token.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void TryDelete(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal sealed record ReportDownload(
        string FilePath,
        string FileName,
        DateTimeOffset ExpiresAt);
}

public static class PressureTestReportEndpointExtensions
{
    public static WebApplication MapDubboTesterPressureTestReports(this WebApplication app)
    {
        app.MapGet(
                "/api/pressure-test/reports/{token}",
                (string token, PressureTestReportDownloadStore store) => DownloadReport(token, store))
            .DisableAntiforgery();
        return app;
    }

    private static IResult DownloadReport(string token, PressureTestReportDownloadStore store)
    {
        if (!store.TryTake(token, out PressureTestReportDownloadStore.ReportDownload download))
        {
            return Results.NotFound();
        }

        try
        {
            var stream = new FileStream(
                download.FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
            return Results.File(
                stream,
                "text/html; charset=utf-8",
                download.FileName,
                enableRangeProcessing: false);
        }
        catch
        {
            store.Delete(download);
            throw;
        }
    }
}
