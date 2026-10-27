using DubboTester.Models;
using DubboTester.Services;
using Xunit;

namespace DubboTester.Tests;

public sealed class DubboPressureTestRunnerTests
{
    [Theory]
    [InlineData(100, PressureTestTcpReuseMode.FullyShared, 1)]
    [InlineData(100, PressureTestTcpReuseMode.PerUser, 100)]
    [InlineData(100, PressureTestTcpReuseMode.SharedBy10Users, 10)]
    [InlineData(101, PressureTestTcpReuseMode.SharedBy10Users, 11)]
    [InlineData(100, PressureTestTcpReuseMode.SharedBy20Users, 5)]
    [InlineData(101, PressureTestTcpReuseMode.SharedBy20Users, 6)]
    public void GetClientCount_UsesConfiguredReuseGroupSize(
        int userCount,
        PressureTestTcpReuseMode reuseMode,
        int expected)
    {
        Assert.Equal(expected, DubboPressureTestRunner.GetClientCount(userCount, reuseMode));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(10, 0)]
    [InlineData(11, 1)]
    [InlineData(20, 1)]
    [InlineData(21, 2)]
    public void GetClientIndex_AssignsTenUsersToEachClient(int userIndex, int expected)
    {
        Assert.Equal(
            expected,
            DubboPressureTestRunner.GetClientIndex(
                25,
                PressureTestTcpReuseMode.SharedBy10Users,
                userIndex));
    }

    [Fact]
    public void ValidateConfiguration_RejectsUnknownTcpReuseMode()
    {
        PressureTestConfiguration configuration = CreateConfiguration();
        configuration.TcpReuseMode = (PressureTestTcpReuseMode)999;

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            DubboPressureTestRunner.ValidateConfiguration(configuration));

        Assert.Contains("TCP 复用模式", exception.Message);
    }

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
    public void ChartMetrics_CanFilterSuccessfulThroughputWithoutChangingAllRequestMetrics()
    {
        PressureTestPoint[] points =
        [
            new(1, 100, 100, 1, 10, 10, 10, 100, SuccessfulRequests: 10),
            new(2, 200, 150, 1, 10, 10, 10, 300, SuccessfulRequests: 30),
            new(3, 150, 150, 1, 10, 10, 10, 450, SuccessfulRequests: 30),
            new(6, 150, 150, 1, 10, 10, 10, 900, SuccessfulRequests: 90)
        ];

        Assert.Equal(150, PressureTestChartMetrics.CalculateSmoothedTps(points, 2, 1));
        Assert.Equal(0, PressureTestChartMetrics.CalculateSmoothedTps(points, 2, 1, successfulOnly: true));
        Assert.Equal(16, PressureTestChartMetrics.CalculateSmoothedTps(points, 3, 5, successfulOnly: true));
        Assert.Equal(20, PressureTestChartMetrics.CalculatePeakSmoothedTps(points, 1, successfulOnly: true));
        Assert.Equal(200, PressureTestChartMetrics.CalculatePeakSmoothedTps(points, 1));
    }

    [Fact]
    public void ChartMetrics_ReturnsZeroSuccessfulThroughputWhenAllRequestsFail()
    {
        PressureTestPoint[] points = [new(1, 100, 100, 1, 10, 10, 10, 100)];

        Assert.Equal(0, PressureTestChartMetrics.CalculateSmoothedTps(points, 0, 5, successfulOnly: true));
        Assert.Equal(0, PressureTestChartMetrics.CalculatePeakSmoothedTps(points, 5, successfulOnly: true));
        Assert.Equal(0, PressureTestChartMetrics.CalculatePeakSmoothedTps([], 5, successfulOnly: true));
    }

    [Fact]
    public void ChartMetrics_UsesSuccessfulInstantTpsWhenSampleTimesAreEqual()
    {
        PressureTestPoint[] points = [new(0, 100, 100, 1, 10, 10, 10, SuccessfulInstantTps: 25)];

        Assert.Equal(25, PressureTestChartMetrics.CalculateSmoothedTps(points, 0, 5, successfulOnly: true));
        Assert.Equal(100, PressureTestChartMetrics.CalculateSmoothedTps(points, 0, 5));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0.07, 0.08)]
    [InlineData(57, 65)]
    [InlineData(99, 100)]
    public void ChartMetrics_CalculatesAdaptivePercentageMaximum(double value, double expected)
    {
        Assert.Equal(expected, PressureTestChartMetrics.CalculateAdaptivePercentageMaximum([value]), 6);
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
    public void LatencyWindow_TracksSuccessfulLatencyAndExpiresSuccessfulSamples()
    {
        var window = new PressureTestLatencyWindow(5);
        window.Add(0, 10, success: true);
        window.Add(1, 1_000, success: false);
        window.Add(4, 30, success: true);

        PressureTestLatencyStatistics first = window.GetStatistics(5);
        Assert.Equal(2, first.SuccessfulRequests);
        Assert.Equal(20, first.SuccessfulAverageMilliseconds);
        Assert.Equal(30, first.SuccessfulP95Milliseconds);
        Assert.Equal(30, first.SuccessfulP99Milliseconds);
        Assert.Equal(1_040d / 3, first.AverageMilliseconds);

        PressureTestLatencyStatistics second = window.GetStatistics(5.1);
        Assert.Equal(1, second.SuccessfulRequests);
        Assert.Equal(30, second.SuccessfulAverageMilliseconds);
        Assert.Equal(30, second.SuccessfulP95Milliseconds);
        Assert.Equal(30, second.SuccessfulP99Milliseconds);
        Assert.Equal(515, second.AverageMilliseconds);

        window.Add(9, 500, success: false);
        PressureTestLatencyStatistics noSuccess = window.GetStatistics(9.1);
        Assert.Equal(0, noSuccess.SuccessfulRequests);
        Assert.Equal(0, noSuccess.SuccessfulAverageMilliseconds);
        Assert.Equal(0, noSuccess.SuccessfulP95Milliseconds);
        Assert.Equal(0, noSuccess.SuccessfulP99Milliseconds);
        Assert.Equal(500, noSuccess.AverageMilliseconds);

        PressureTestLatencyStatistics empty = window.GetStatistics(15);
        Assert.Equal(0, empty.SuccessfulRequests);
        Assert.Equal(0, empty.SuccessfulAverageMilliseconds);
    }

    [Fact]
    public void LatencyWindow_SamplesSuccessfulPercentilesIndependentlyOfFailedRequests()
    {
        var window = new PressureTestLatencyWindow(5, maximumPercentileSamples: 2);
        for (int index = 0; index < 10; index++) window.Add(1, 1_000, success: false);
        window.Add(1, 30, success: true);
        window.Add(1, 70, success: true);

        PressureTestLatencyStatistics statistics = window.GetStatistics(2);
        Assert.Equal(2, statistics.SuccessfulRequests);
        Assert.Equal(50, statistics.SuccessfulAverageMilliseconds);
        Assert.Equal(70, statistics.SuccessfulP95Milliseconds);
        Assert.Equal(70, statistics.SuccessfulP99Milliseconds);
        Assert.Equal(1_000, statistics.P95Milliseconds);
    }

    [Fact]
    public void RunState_KeepsFullSuccessfulMetricsWhenLiveResultsAreTruncated()
    {
        var state = new DubboPressureTestRunner.RunState(300, 0, false, 500);
        state.Start();
        for (int index = 1; index <= 300; index++)
        {
            bool success = index % 2 == 1;
            state.Record(1, index, success ? 10 : 100, success, success ? null : "failed", null, null);
        }
        state.Stop();

        PressureTestSnapshot live = state.CreateSnapshot(isRunning: true, isCancelled: false, addPoint: true);
        Assert.Equal(200, live.RequestResults.Count);
        Assert.Equal(300, live.CompletedRequests);
        Assert.Equal(150, live.SuccessCount);
        Assert.Equal(55, live.AverageMilliseconds);
        Assert.Equal(10, live.SuccessfulAverageMilliseconds);
        Assert.Equal(150 / live.ElapsedSeconds, live.SuccessfulTps);
        Assert.Equal(150, live.Timeline[^1].SuccessfulRequests);
        Assert.Equal(150, live.Timeline[^1].RecentSuccessfulRequests);
        Assert.Equal(10, live.Timeline[^1].RecentSuccessfulAverageMilliseconds);
        Assert.Equal(10, live.Timeline[^1].SuccessfulAverageMilliseconds);
        Assert.Equal(10, live.Timeline[^1].SuccessfulP95Milliseconds);
        Assert.Equal(10, live.Timeline[^1].SuccessfulP99Milliseconds);
        Assert.Equal(10, live.Timeline[^1].RecentSuccessfulP95Milliseconds);
        Assert.Equal(10, live.Timeline[^1].RecentSuccessfulP99Milliseconds);
        Assert.Equal(100, live.Timeline[^1].P95Milliseconds);
        Assert.Equal(live.SuccessfulTps, live.Timeline[^1].SuccessfulInstantTps);

        PressureTestSnapshot final = state.CreateSnapshot(isRunning: false, isCancelled: false, addPoint: true);
        Assert.Equal(300, final.RequestResults.Count);
        Assert.Equal(live.SuccessfulAverageMilliseconds, final.SuccessfulAverageMilliseconds);
        Assert.Equal(live.SuccessfulTps, final.SuccessfulTps);
    }

    [Fact]
    public void RunState_ReturnsZeroSuccessfulMetricsForEmptyAndAllFailedRuns()
    {
        var state = new DubboPressureTestRunner.RunState(1, 0, false, 500);
        PressureTestSnapshot empty = state.CreateSnapshot(isRunning: true, isCancelled: false, addPoint: true);
        Assert.Equal(0, empty.SuccessfulTps);
        Assert.Equal(0, empty.SuccessfulAverageMilliseconds);

        state.Start();
        state.Record(1, 1, 100, false, "failed", null, null);
        state.Stop();
        PressureTestSnapshot failed = state.CreateSnapshot(isRunning: false, isCancelled: false, addPoint: true);
        Assert.Equal(1, failed.FailureCount);
        Assert.Equal(100, failed.AverageMilliseconds);
        Assert.Equal(0, failed.SuccessfulAverageMilliseconds);
        Assert.Equal(0, failed.SuccessfulTps);
        Assert.Equal(0, failed.Timeline[^1].SuccessfulRequests);
        Assert.Equal(0, failed.Timeline[^1].RecentSuccessfulRequests);
        Assert.Equal(0, failed.Timeline[^1].SuccessfulP95Milliseconds);
        Assert.Equal(0, failed.Timeline[^1].SuccessfulP99Milliseconds);
    }

    [Fact]
    public void RunState_FinalSuccessfulPercentilesUseFullDataBeyondLiveSampleLimit()
    {
        var state = new DubboPressureTestRunner.RunState(21_001, 0, false, 500);
        state.Start();
        for (int index = 1; index <= 21_000; index++)
        {
            state.Record(1, index, index, true, null, null, null);
        }

        PressureTestSnapshot live = state.CreateSnapshot(isRunning: true, isCancelled: false, addPoint: true);
        Assert.True(live.PercentilesAreSampled);
        Assert.Equal(live.P95Milliseconds, live.Timeline[^1].SuccessfulP95Milliseconds);
        Assert.Equal(live.P99Milliseconds, live.Timeline[^1].SuccessfulP99Milliseconds);

        state.Record(1, 21_001, 1_000_000, false, "failed", null, null);
        state.Stop();
        PressureTestSnapshot final = state.CreateSnapshot(isRunning: false, isCancelled: false, addPoint: true);
        Assert.False(final.PercentilesAreSampled);
        Assert.Equal(19_950, final.Timeline[^1].SuccessfulP95Milliseconds);
        Assert.Equal(20_790, final.Timeline[^1].SuccessfulP99Milliseconds);
        Assert.NotEqual(final.P95Milliseconds, final.Timeline[^1].SuccessfulP95Milliseconds);
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
