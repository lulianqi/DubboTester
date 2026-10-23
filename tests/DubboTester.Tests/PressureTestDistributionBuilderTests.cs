using DubboTester.Models;
using DubboTester.Services;
using Xunit;

namespace DubboTester.Tests;

public sealed class PressureTestDistributionBuilderTests
{
    [Fact]
    public void Build_GroupsFailuresAndSlowRequestsIntoTimeBuckets()
    {
        PressureTestRequestResult[] results =
        [
            new(1, 1, 40, .5, true, null),
            new(1, 2, 120, 1.5, false, "failed"),
            new(2, 1, 250, 2.5, true, null),
            new(2, 2, 80, 3.5, false, "failed")
        ];

        IReadOnlyList<PressureTestDistributionBucket> buckets =
            PressureTestDistributionBuilder.Build(results, 4, 100, maximumBuckets: 10);

        Assert.Equal(2, buckets.Count);
        Assert.Equal((2, 1, 1), (
            buckets[0].TotalRequests,
            buckets[0].FailedRequests,
            buckets[0].SlowRequests));
        Assert.Equal((2, 1, 1), (
            buckets[1].TotalRequests,
            buckets[1].FailedRequests,
            buckets[1].SlowRequests));
        Assert.Equal(50, buckets[0].ErrorRate);
        Assert.Equal(50, buckets[1].SlowRate);
    }

    [Fact]
    public void Build_TreatsThresholdAsStrictlyGreaterThan()
    {
        PressureTestRequestResult[] results =
        [
            new(1, 1, 100, .1, true, null),
            new(1, 2, 100.01, .2, true, null)
        ];

        PressureTestDistributionBucket bucket = Assert.Single(
            PressureTestDistributionBuilder.Build(results, 1, 100));

        Assert.Equal(1, bucket.SlowRequests);
        Assert.Equal(50, bucket.SlowRate);
    }
}
