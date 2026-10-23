using DubboTester.Models;

namespace DubboTester.Services;

public sealed record PressureTestReportExportRequest(
    PressureTestConfiguration Configuration,
    PressureTestSnapshot Snapshot,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    bool IncludeAllRequests,
    int ThroughputSmoothingSeconds,
    bool UseRecentLatency,
    string FileName);

public sealed record PressureTestReportExportResult(
    string FileName,
    string? DownloadUrl = null,
    string? SavedPath = null);

public interface IPressureTestReportExportService
{
    Task<PressureTestReportExportResult> ExportAsync(
        PressureTestReportExportRequest request,
        CancellationToken cancellationToken = default);
}
