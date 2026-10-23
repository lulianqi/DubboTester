using DubboTester.Models;

namespace DubboTester.Services;

internal sealed class PressureTestPayloadRetention(long maximumBytes)
{
    private const int ApproximateStringOverheadBytes = 24;
    private readonly Queue<int> _successfulPayloads = new();
    private readonly Queue<int> _failedPayloads = new();

    public long ApproximateBytes { get; private set; }
    public int DiscardedCount { get; private set; }

    public void TrackAndTrim(List<PressureTestRequestResult> results)
    {
        if (results.Count == 0) return;

        int index = results.Count - 1;
        PressureTestRequestResult result = results[index];
        long payloadBytes = EstimatePayloadBytes(result.Request, result.Response);
        if (payloadBytes == 0) return;

        ApproximateBytes = checked(ApproximateBytes + payloadBytes);
        (result.Success ? _successfulPayloads : _failedPayloads).Enqueue(index);

        // Successful payloads are expendable first, even when a failed request is older.
        TrimQueue(_successfulPayloads, results);
        TrimQueue(_failedPayloads, results);
    }

    internal static long EstimatePayloadBytes(string? request, string? response) =>
        EstimateStringBytes(request) + EstimateStringBytes(response);

    private void TrimQueue(
        Queue<int> candidates,
        List<PressureTestRequestResult> results)
    {
        while (ApproximateBytes > maximumBytes && candidates.TryDequeue(out int index))
        {
            PressureTestRequestResult result = results[index];
            long payloadBytes = EstimatePayloadBytes(result.Request, result.Response);
            if (payloadBytes == 0) continue;

            ApproximateBytes = Math.Max(0, ApproximateBytes - payloadBytes);
            results[index] = result with
            {
                Request = null,
                Response = null,
                PayloadDiscarded = true
            };
            DiscardedCount++;
        }
    }

    private static long EstimateStringBytes(string? value)
    {
        if (value is null) return 0;

        // .NET strings use UTF-16. Object/header/alignment costs vary by runtime,
        // so this deliberately remains a conservative approximation.
        long bytes = ApproximateStringOverheadBytes + (long)value.Length * sizeof(char);
        return (bytes + 7) & ~7L;
    }
}
