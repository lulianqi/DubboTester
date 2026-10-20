using DubboTester.Models;
using DubboTester.Services;
using Xunit;

namespace DubboTester.Tests;

public sealed class DubboPressureTestRunnerTests
{
    [Fact]
    public void CalculateUserStartDelay_StartsEachStepAtItsAbsoluteScheduledTime()
    {
        PressureTestConfiguration configuration = CreateConfiguration();
        configuration.UserCount = 5;
        configuration.UserStartIntervalMs = 1_500;
        configuration.StepStartIntervalSeconds = 1;
        configuration.StepUserCount = 2;

        long[] delays = Enumerable.Range(1, configuration.UserCount)
            .Select(userIndex =>
                DubboPressureTestRunner.CalculateUserStartDelayMilliseconds(
                    configuration,
                    userIndex))
            .ToArray();

        Assert.Equal([0L, 1_500L, 1_000L, 2_500L, 2_000L], delays);
        Assert.True(delays[2] < delays[1]);
        Assert.True(delays[4] < delays[3]);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(5, 0)]
    public void CalculateUserStartDelay_DisablesStepsWhenEitherStepOptionIsZero(
        int stepIntervalSeconds,
        int stepUserCount)
    {
        PressureTestConfiguration configuration = CreateConfiguration();
        configuration.UserCount = 4;
        configuration.UserStartIntervalMs = 200;
        configuration.StepStartIntervalSeconds = stepIntervalSeconds;
        configuration.StepUserCount = stepUserCount;

        long[] delays = Enumerable.Range(1, configuration.UserCount)
            .Select(userIndex =>
                DubboPressureTestRunner.CalculateUserStartDelayMilliseconds(
                    configuration,
                    userIndex))
            .ToArray();

        Assert.Equal([0L, 200L, 400L, 600L], delays);
    }

    [Fact]
    public void ValidateConfiguration_RejectsStepUserCountAboveConcurrency()
    {
        PressureTestConfiguration configuration = CreateConfiguration();
        configuration.UserCount = 10;
        configuration.StepUserCount = 11;

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            DubboPressureTestRunner.ValidateConfiguration(configuration));

        Assert.Contains("阶梯用户数", exception.Message);
    }

    [Fact]
    public void ValidateConfiguration_RejectsStepIntervalAboveDurationLimit()
    {
        PressureTestConfiguration configuration = CreateConfiguration();
        configuration.LimitMode = PressureTestLimitMode.Duration;
        configuration.DurationLimitSeconds = 10;
        configuration.StepStartIntervalSeconds = 11;

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            DubboPressureTestRunner.ValidateConfiguration(configuration));

        Assert.Contains("阶梯启动间隔不能大于执行时长", exception.Message);
    }

    [Theory]
    [InlineData(9.999, true)]
    [InlineData(10, false)]
    [InlineData(10.001, false)]
    public void ShouldStartRequest_UsesGlobalDurationLimit(
        double elapsedSeconds,
        bool expected)
    {
        PressureTestConfiguration configuration = CreateConfiguration();
        configuration.LimitMode = PressureTestLimitMode.Duration;
        configuration.DurationLimitSeconds = 10;

        bool result = DubboPressureTestRunner.ShouldStartRequest(
            configuration,
            iterationIndex: 999_999,
            elapsedSeconds);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ShouldStartRequest_KeepsExistingPerUserRequestLimitMode()
    {
        PressureTestConfiguration configuration = CreateConfiguration();
        configuration.IterationsPerUser = 3;

        Assert.True(DubboPressureTestRunner.ShouldStartRequest(configuration, 3, 999));
        Assert.False(DubboPressureTestRunner.ShouldStartRequest(configuration, 4, 0));
    }

    [Fact]
    public void SnapshotProgress_UsesDurationWhenDurationLimitIsPresent()
    {
        var snapshot = new PressureTestSnapshot
        {
            DurationLimitSeconds = 20,
            ElapsedSeconds = 5,
            CompletedRequests = 50
        };

        Assert.Equal(25, snapshot.ProgressPercent);
    }

    [Fact]
    public void ThroughputWindow_RecordsZeroRawSamplesAndSmoothsFromStart()
    {
        var window = new PressureTestThroughputWindow(5);

        PressureTestThroughputSample first = window.AddSample(.5, 0);
        PressureTestThroughputSample second = window.AddSample(1, 1);
        PressureTestThroughputSample third = window.AddSample(1.5, 1);

        Assert.Equal(0, first.RawTps);
        Assert.Equal(2, second.RawTps);
        Assert.Equal(1, second.SmoothedTps);
        Assert.Equal(0, third.RawTps);
        Assert.Equal(2d / 3, third.SmoothedTps, precision: 10);
    }

    [Fact]
    public void ThroughputWindow_UsesFiveSecondSlidingWindowAndTracksPeak()
    {
        var window = new PressureTestThroughputWindow(5);
        PressureTestThroughputSample sample = default;
        for (int index = 1; index <= 10; index++)
        {
            sample = window.AddSample(index * .5, index * 10);
        }

        Assert.Equal(20, sample.SmoothedTps);
        Assert.Equal(20, window.PeakSmoothedTps);

        sample = window.AddSample(5.5, 100);

        Assert.Equal(0, sample.RawTps);
        Assert.Equal(18, sample.SmoothedTps);
        Assert.Equal(20, window.PeakSmoothedTps);
    }

    [Fact]
    public void ChartMetrics_RecalculatesThroughputForSelectedWindow()
    {
        PressureTestPoint[] points =
        [
            new(.5, 0, 0, 1, 10, 10, 10, 0),
            new(1, 10, 10, 1, 10, 10, 10, 5),
            new(1.5, 10, 10, 1, 10, 10, 10, 10),
            new(2, 20, 10, 1, 10, 10, 10, 20)
        ];

        Assert.Equal(15, PressureTestChartMetrics.CalculateSmoothedTps(points, 3, 1));
        Assert.Equal(10, PressureTestChartMetrics.CalculateSmoothedTps(points, 3, 5));
        Assert.Equal(15, PressureTestChartMetrics.CalculatePeakSmoothedTps(points, 1));
    }

    [Fact]
    public void LatencyWindow_UsesOnlyRequestsCompletedInRecentFiveSeconds()
    {
        var window = new PressureTestLatencyWindow(5);
        window.Add(0, 10);
        window.Add(1, 20);
        window.Add(4.9, 30);

        PressureTestLatencyStatistics first = window.GetStatistics(5);
        Assert.Equal(20, first.AverageMilliseconds);
        Assert.Equal(30, first.P95Milliseconds);
        Assert.Equal(30, first.P99Milliseconds);

        window.Add(5.1, 40);
        PressureTestLatencyStatistics second = window.GetStatistics(5.1);
        Assert.Equal(30, second.AverageMilliseconds);
        Assert.Equal(40, second.P95Milliseconds);
        Assert.Equal(40, second.P99Milliseconds);
    }

    [Fact]
    public void PayloadRetention_DiscardsSuccessfulPayloadBeforeFailedPayloads()
    {
        string request = new('q', 20);
        string response = new('a', 20);
        long itemBytes = PressureTestPayloadRetention.EstimatePayloadBytes(request, response);
        var retention = new PressureTestPayloadRetention(itemBytes * 2);
        var results = new List<PressureTestRequestResult>();

        AddResult(results, retention, success: false, request, response);
        AddResult(results, retention, success: true, request, response);
        AddResult(results, retention, success: false, request, response);

        Assert.False(results[0].PayloadDiscarded);
        Assert.True(results[1].PayloadDiscarded);
        Assert.Null(results[1].Request);
        Assert.Null(results[1].Response);
        Assert.False(results[2].PayloadDiscarded);
        Assert.Equal(1, retention.DiscardedCount);
        Assert.Equal(itemBytes * 2, retention.ApproximateBytes);
    }

    [Fact]
    public void PayloadRetention_DiscardsOldestFailureWhenFailuresExceedLimit()
    {
        string request = new('q', 20);
        string response = new('a', 20);
        long itemBytes = PressureTestPayloadRetention.EstimatePayloadBytes(request, response);
        var retention = new PressureTestPayloadRetention(itemBytes * 2);
        var results = new List<PressureTestRequestResult>();

        AddResult(results, retention, success: false, request, response);
        AddResult(results, retention, success: false, request, response);
        AddResult(results, retention, success: false, request, response);

        Assert.True(results[0].PayloadDiscarded);
        Assert.False(results[1].PayloadDiscarded);
        Assert.False(results[2].PayloadDiscarded);
        Assert.Equal(1, retention.DiscardedCount);
        Assert.Equal(itemBytes * 2, retention.ApproximateBytes);
    }

    private static void AddResult(
        List<PressureTestRequestResult> results,
        PressureTestPayloadRetention retention,
        bool success,
        string request,
        string response)
    {
        results.Add(new PressureTestRequestResult(
            UserIndex: 1,
            IterationIndex: results.Count + 1,
            ElapsedMilliseconds: 1,
            CompletedAtSeconds: results.Count + 1,
            Success: success,
            Error: success ? null : "failed",
            Request: request,
            Response: response));
        retention.TrackAndTrim(results);
    }

    private static PressureTestConfiguration CreateConfiguration() => new()
    {
        Settings = new TesterSettings(),
        Invocation = new InvocationInput(),
        UserCount = 10,
        IterationsPerUser = 1
    };
}
