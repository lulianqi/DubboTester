using DubboTester.Models;

namespace DubboTester.Services;

public static class PressureTestDistributionBuilder
{
    private const double TargetBucketSeconds = 2;

    public static IReadOnlyList<PressureTestDistributionBucket> Build(
        IReadOnlyList<PressureTestRequestResult> results,
        double elapsedSeconds,
        double slowRequestThresholdMilliseconds,
        int maximumBuckets = 60)
    {
        if (results.Count == 0 || maximumBuckets <= 0) return [];

        double duration = Math.Max(
            Math.Max(0, elapsedSeconds),
            results.Max(result => Math.Max(0, result.CompletedAtSeconds)));
        duration = Math.Max(duration, .001);
        int bucketCount = Math.Clamp(
            (int)Math.Ceiling(duration / TargetBucketSeconds),
            1,
            maximumBuckets);
        double bucketWidth = duration / bucketCount;
        var totals = new int[bucketCount];
        var failures = new int[bucketCount];
        var slowRequests = new int[bucketCount];
        double threshold = Math.Max(0, slowRequestThresholdMilliseconds);

        foreach (PressureTestRequestResult result in results)
        {
            int index = Math.Clamp(
                (int)Math.Floor(Math.Max(0, result.CompletedAtSeconds) / bucketWidth),
                0,
                bucketCount - 1);
            totals[index]++;
            if (!result.Success) failures[index]++;
            if (result.ElapsedMilliseconds > threshold) slowRequests[index]++;
        }

        return Enumerable.Range(0, bucketCount)
            .Select(index => new PressureTestDistributionBucket(
                index * bucketWidth,
                (index + 1) * bucketWidth,
                totals[index],
                failures[index],
                slowRequests[index]))
            .ToArray();
    }
}
