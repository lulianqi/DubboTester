using System.Diagnostics;
using DubboTester.Models;

namespace DubboTester.Services;

public sealed class DubboPressureTestRunner(DubboSession session)
{
    public const long MaximumRetainedPayloadBytes = 100L * 1024 * 1024;
    private const int MaximumTotalRequests = 1_000_000;
    private const int MaximumIntervalMilliseconds = 3_600_000;
    private const int MaximumStepIntervalSeconds = 3_600;
    private const int MaximumDurationLimitSeconds = 86_400;
    private const int MaximumRetainedErrorCharacters = 4_096;

    public async Task<PressureTestSnapshot> RunAsync(
        PressureTestConfiguration configuration,
        IProgress<PressureTestSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration(configuration);
        string sample = PressureTestTemplateResolver.Resolve(
            configuration.Invocation.RequestJson,
            1,
            1);
        JsonArgumentParser.ParseArguments(sample);
        PressureTestBusinessAssertionEvaluator.Validate(configuration.BusinessAssertion);

        DubboSession[] invocationSessions = CreateInvocationSessions(configuration);
        try
        {
            await Task.WhenAll(invocationSessions.Select(invocationSession =>
                invocationSession.PrepareForInvocationsAsync(
                    configuration.Settings,
                    configuration.Invocation,
                    cancellationToken)));

            var state = new RunState(
                configuration.TotalRequests,
                configuration.LimitMode == PressureTestLimitMode.Duration
                    ? configuration.DurationLimitSeconds
                    : 0,
                configuration.RecordRequestResponse,
                configuration.SlowRequestThresholdMilliseconds);
            var startGate = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Task[] workers = Enumerable.Range(1, configuration.UserCount)
                .Select(userIndex => RunUserAsync(
                    invocationSessions[GetClientIndex(
                        configuration.UserCount,
                        configuration.TcpReuseMode,
                        userIndex)],
                    configuration,
                    userIndex,
                    state,
                    startGate.Task,
                    cancellationToken))
                .ToArray();
            Task allWorkers = Task.WhenAll(workers);
            state.Start();
            startGate.SetResult(true);
            Task reporter = ReportProgressAsync(allWorkers, state, progress);

            bool cancelled = false;
            try
            {
                await allWorkers;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
            }
            finally
            {
                state.Stop();
                await reporter;
            }

            PressureTestSnapshot final = state.CreateSnapshot(isRunning: false, isCancelled: cancelled, addPoint: true);
            progress?.Report(final);
            return final;
        }
        finally
        {
            foreach (DubboSession additionalSession in invocationSessions.Skip(1))
            {
                additionalSession.Dispose();
            }
        }
    }

    private async Task RunUserAsync(
        DubboSession invocationSession,
        PressureTestConfiguration configuration,
        int userIndex,
        RunState state,
        Task startSignal,
        CancellationToken cancellationToken)
    {
        await startSignal.WaitAsync(cancellationToken);
        long userStartDelay = CalculateUserStartDelayMilliseconds(configuration, userIndex);
        if (!await DelayWithinDurationAsync(
                userStartDelay,
                configuration,
                state,
                cancellationToken))
        {
            return;
        }

        state.ChangeActiveUsers(1);
        try
        {
            for (int iterationIndex = 1;
                 ShouldStartRequest(configuration, iterationIndex, state.ElapsedSeconds);
                 iterationIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (iterationIndex > 1 && configuration.RequestIntervalMs > 0)
                {
                    if (!await DelayWithinDurationAsync(
                            configuration.RequestIntervalMs,
                            configuration,
                            state,
                            cancellationToken))
                    {
                        break;
                    }
                }

                if (!ShouldStartRequest(configuration, iterationIndex, state.ElapsedSeconds)) break;

                InvocationInput invocation = configuration.Invocation.Clone();
                invocation.RequestJson = PressureTestTemplateResolver.Resolve(
                    configuration.Invocation.RequestJson,
                    userIndex,
                    iterationIndex);

                Stopwatch stopwatch = Stopwatch.StartNew();
                bool success;
                string? error = null;
                string? response = null;
                try
                {
                    InvocationOutput output = await invocationSession.InvokeAsync(
                        configuration.Settings,
                        invocation,
                        cancellationToken);
                    success = output.Success;
                    error = output.Success ? null : output.Error ?? "Dubbo 请求失败。";
                    response = output.Success ? output.Content : output.Error;
                    if (success && configuration.BusinessAssertion.Conditions.Count > 0)
                    {
                        BusinessAssertionEvaluation assertion =
                            PressureTestBusinessAssertionEvaluator.Evaluate(
                                configuration.BusinessAssertion,
                                output.Content);
                        success = assertion.Success;
                        error = assertion.Error;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    success = false;
                    error = GetErrorMessage(exception);
                    response = error;
                }
                finally
                {
                    stopwatch.Stop();
                }

                state.Record(
                    userIndex,
                    iterationIndex,
                    stopwatch.Elapsed.TotalMilliseconds,
                    success,
                    error,
                    configuration.RecordRequestResponse ? invocation.RequestJson : null,
                    configuration.RecordRequestResponse ? response : null);
            }
        }
        finally
        {
            state.ChangeActiveUsers(-1);
        }
    }

    private static async Task ReportProgressAsync(
        Task workers,
        RunState state,
        IProgress<PressureTestSnapshot>? progress)
    {
        if (progress is null) return;
        while (!workers.IsCompleted)
        {
            Task delay = Task.Delay(500);
            if (await Task.WhenAny(workers, delay) == workers) break;
            progress.Report(state.CreateSnapshot(isRunning: true, isCancelled: false, addPoint: true));
        }
    }

    public static void ValidateConfiguration(PressureTestConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(configuration.Settings);
        ArgumentNullException.ThrowIfNull(configuration.Invocation);
        if (configuration.UserCount is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(configuration.UserCount), "并发用户数必须在 1 到 500 之间。");
        if (!Enum.IsDefined(configuration.TcpReuseMode))
            throw new ArgumentOutOfRangeException(nameof(configuration.TcpReuseMode), "请选择有效的 TCP 复用模式。");
        if (!Enum.IsDefined(configuration.LimitMode))
            throw new ArgumentOutOfRangeException(nameof(configuration.LimitMode), "请选择有效的负载上限模式。");
        if (configuration.LimitMode == PressureTestLimitMode.RequestCount
            && configuration.IterationsPerUser is < 1 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(configuration.IterationsPerUser), "每用户发送次数必须在 1 到 100000 之间。");
        if (configuration.LimitMode == PressureTestLimitMode.Duration
            && configuration.DurationLimitSeconds is < 1 or > MaximumDurationLimitSeconds)
            throw new ArgumentOutOfRangeException(nameof(configuration.DurationLimitSeconds), "执行时长必须在 1 到 86400 秒之间。");
        if (configuration.UserStartIntervalMs is < 0 or > MaximumIntervalMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(configuration.UserStartIntervalMs), "用户启动间隔必须在 0 到 3600000 毫秒之间。");
        if (configuration.StepStartIntervalSeconds is < 0 or > MaximumStepIntervalSeconds)
            throw new ArgumentOutOfRangeException(nameof(configuration.StepStartIntervalSeconds), "阶梯启动间隔必须在 0 到 3600 秒之间。");
        if (configuration.LimitMode == PressureTestLimitMode.Duration
            && configuration.StepStartIntervalSeconds > configuration.DurationLimitSeconds)
            throw new ArgumentOutOfRangeException(nameof(configuration.StepStartIntervalSeconds), "时间上限模式下，阶梯启动间隔不能大于执行时长。");
        if (configuration.StepUserCount < 0 || configuration.StepUserCount > configuration.UserCount)
            throw new ArgumentOutOfRangeException(nameof(configuration.StepUserCount), "阶梯用户数必须在 0 到并发用户数之间。");
        if (configuration.RequestIntervalMs is < 0 or > MaximumIntervalMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(configuration.RequestIntervalMs), "请求间隔必须在 0 到 3600000 毫秒之间。");
        if (!double.IsFinite(configuration.SlowRequestThresholdMilliseconds)
            || configuration.SlowRequestThresholdMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(configuration.SlowRequestThresholdMilliseconds), "慢请求阈值必须大于 0 毫秒。");
        if (configuration.LimitMode == PressureTestLimitMode.RequestCount
            && (long)configuration.UserCount * configuration.IterationsPerUser > MaximumTotalRequests)
            throw new ArgumentOutOfRangeException(nameof(configuration.IterationsPerUser), $"单次压测最多允许 {MaximumTotalRequests:N0} 个请求。");
    }

    public static int GetClientCount(
        int userCount,
        PressureTestTcpReuseMode reuseMode)
    {
        if (userCount < 1)
            throw new ArgumentOutOfRangeException(nameof(userCount));
        int usersPerClient = GetUsersPerClient(userCount, reuseMode);
        return (userCount + usersPerClient - 1) / usersPerClient;
    }

    internal static int GetClientIndex(
        int userCount,
        PressureTestTcpReuseMode reuseMode,
        int userIndex)
    {
        if (userIndex < 1 || userIndex > userCount)
            throw new ArgumentOutOfRangeException(nameof(userIndex));
        return (userIndex - 1) / GetUsersPerClient(userCount, reuseMode);
    }

    private DubboSession[] CreateInvocationSessions(PressureTestConfiguration configuration)
    {
        int clientCount = GetClientCount(configuration.UserCount, configuration.TcpReuseMode);
        var result = new DubboSession[clientCount];
        result[0] = session;
        for (int index = 1; index < result.Length; index++)
        {
            result[index] = session.CreateInvocationSession();
        }
        return result;
    }

    private static int GetUsersPerClient(
        int userCount,
        PressureTestTcpReuseMode reuseMode) => reuseMode switch
        {
            PressureTestTcpReuseMode.FullyShared => userCount,
            PressureTestTcpReuseMode.PerUser => 1,
            PressureTestTcpReuseMode.SharedBy10Users => 10,
            PressureTestTcpReuseMode.SharedBy20Users => 20,
            _ => throw new ArgumentOutOfRangeException(nameof(reuseMode), "请选择有效的 TCP 复用模式。")
        };

    public static bool ShouldStartRequest(
        PressureTestConfiguration configuration,
        int iterationIndex,
        double elapsedSeconds)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.LimitMode switch
        {
            PressureTestLimitMode.RequestCount => iterationIndex <= configuration.IterationsPerUser,
            PressureTestLimitMode.Duration => elapsedSeconds < configuration.DurationLimitSeconds,
            _ => false
        };
    }

    public static long CalculateUserStartDelayMilliseconds(
        PressureTestConfiguration configuration,
        int userIndex)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (userIndex < 1 || userIndex > configuration.UserCount)
            throw new ArgumentOutOfRangeException(nameof(userIndex), "用户序号必须在并发用户范围内。");

        int zeroBasedUserIndex = userIndex - 1;
        if (configuration.StepStartIntervalSeconds == 0 || configuration.StepUserCount == 0)
        {
            return checked((long)zeroBasedUserIndex * configuration.UserStartIntervalMs);
        }

        int stepIndex = zeroBasedUserIndex / configuration.StepUserCount;
        int userIndexWithinStep = zeroBasedUserIndex % configuration.StepUserCount;
        long stepStartDelay = checked((long)stepIndex * configuration.StepStartIntervalSeconds * 1_000);
        long delayWithinStep = checked((long)userIndexWithinStep * configuration.UserStartIntervalMs);
        return checked(stepStartDelay + delayWithinStep);
    }

    public static double Percentile(IReadOnlyList<double> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0) return 0;
        int index = Math.Clamp(
            (int)Math.Ceiling(sortedValues.Count * percentile) - 1,
            0,
            sortedValues.Count - 1);
        return sortedValues[index];
    }

    private static async Task<bool> DelayWithinDurationAsync(
        long delayMilliseconds,
        PressureTestConfiguration configuration,
        RunState state,
        CancellationToken cancellationToken)
    {
        if (configuration.LimitMode != PressureTestLimitMode.Duration)
        {
            if (delayMilliseconds > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(delayMilliseconds), cancellationToken);
            }
            return true;
        }

        long remainingMilliseconds = state.GetRemainingDurationMilliseconds(
            configuration.DurationLimitSeconds);
        if (remainingMilliseconds <= 0) return false;
        if (delayMilliseconds <= 0) return true;

        long actualDelay = Math.Min(delayMilliseconds, remainingMilliseconds);
        await Task.Delay(TimeSpan.FromMilliseconds(actualDelay), cancellationToken);
        return delayMilliseconds < remainingMilliseconds;
    }

    private static string GetErrorMessage(Exception exception)
    {
        Exception current = exception;
        while (current.InnerException is not null) current = current.InnerException;
        return current.Message;
    }

    private sealed class RunState(
        int totalRequests,
        int durationLimitSeconds,
        bool recordsRequestResponse,
        double slowRequestThresholdMilliseconds)
    {
        private const int MaximumTimelinePoints = 600;
        private const int MaximumLiveResults = 200;
        private const int MaximumLivePercentileSamples = 20_000;
        private readonly object _sync = new();
        private readonly Stopwatch _stopwatch = new();
        private readonly List<double> _elapsed = [];
        private readonly List<double> _percentileSample = [];
        private readonly List<PressureTestPoint> _timeline = [];
        private readonly List<PressureTestRequestResult> _requestResults = [];
        private readonly PressureTestPayloadRetention _payloadRetention = new(MaximumRetainedPayloadBytes);
        private readonly PressureTestThroughputWindow _throughputWindow = new(5);
        private readonly PressureTestLatencyWindow _recentLatencyWindow = new(5);
        private readonly PressureTestDistributionAccumulator _distribution =
            new(slowRequestThresholdMilliseconds);
        private int _successCount;
        private int _failureCount;
        private int _activeUsers;
        private DateTimeOffset _startedAt;
        private double _elapsedTotal;
        private double _minimum = double.MaxValue;
        private double _maximum;

        public void Start()
        {
            _startedAt = DateTimeOffset.Now;
            _stopwatch.Start();
        }
        public void Stop() => _stopwatch.Stop();
        public double ElapsedSeconds => _stopwatch.Elapsed.TotalSeconds;

        public long GetRemainingDurationMilliseconds(int limitSeconds)
        {
            double remaining = limitSeconds - _stopwatch.Elapsed.TotalSeconds;
            return remaining <= 0 ? 0 : (long)Math.Ceiling(remaining * 1_000);
        }

        public void ChangeActiveUsers(int change)
        {
            lock (_sync) _activeUsers += change;
        }

        public void Record(
            int userIndex,
            int iterationIndex,
            double elapsedMilliseconds,
            bool success,
            string? error,
            string? request,
            string? response)
        {
            lock (_sync)
            {
                double elapsed = Math.Max(0, elapsedMilliseconds);
                double completedAtSeconds = _stopwatch.Elapsed.TotalSeconds;
                _elapsed.Add(elapsed);
                _elapsedTotal += elapsed;
                _recentLatencyWindow.Add(completedAtSeconds, elapsed);
                _minimum = Math.Min(_minimum, elapsed);
                _maximum = Math.Max(_maximum, elapsed);
                if (_percentileSample.Count < MaximumLivePercentileSamples)
                {
                    _percentileSample.Add(elapsed);
                }
                else
                {
                    long replacementIndex = Random.Shared.NextInt64(_elapsed.Count);
                    if (replacementIndex < MaximumLivePercentileSamples)
                    {
                        _percentileSample[(int)replacementIndex] = elapsed;
                    }
                }
                if (success)
                {
                    _successCount++;
                }
                else
                {
                    _failureCount++;
                }
                _distribution.Record(completedAtSeconds, elapsed, success);

                _requestResults.Add(new PressureTestRequestResult(
                    userIndex,
                    iterationIndex,
                    elapsed,
                    completedAtSeconds,
                    success,
                    success ? null : RetainErrorSummary(error),
                    recordsRequestResponse ? request : null,
                    recordsRequestResponse ? response : null));
                _payloadRetention.TrackAndTrim(_requestResults);
            }
        }

        private static string RetainErrorSummary(string? error)
        {
            string value = error ?? "请求失败。";
            return value.Length <= MaximumRetainedErrorCharacters
                ? value
                : string.Concat(
                    value.AsSpan(0, MaximumRetainedErrorCharacters),
                    "…（错误信息过长，已截断）");
        }

        public PressureTestSnapshot CreateSnapshot(bool isRunning, bool isCancelled, bool addPoint)
        {
            lock (_sync)
            {
                double elapsedSeconds = _stopwatch.Elapsed.TotalSeconds;
                int completed = _successCount + _failureCount;
                IReadOnlyList<double> percentileSource = isRunning ? _percentileSample : _elapsed;
                double[] sorted = percentileSource.Order().ToArray();
                double average = completed == 0 ? 0 : _elapsedTotal / completed;
                double p95 = Percentile(sorted, .95);
                double p99 = Percentile(sorted, .99);
                PressureTestLatencyStatistics recentLatency =
                    _recentLatencyWindow.GetStatistics(elapsedSeconds);
                double tps = elapsedSeconds <= 0 ? 0 : completed / elapsedSeconds;
                PressureTestRequestResult[] requestResults;
                if (isRunning && _requestResults.Count > MaximumLiveResults)
                {
                    requestResults = _requestResults
                        .GetRange(_requestResults.Count - MaximumLiveResults, MaximumLiveResults)
                        .ToArray();
                }
                else
                {
                    requestResults = _requestResults.ToArray();
                }

                if (addPoint)
                {
                    PressureTestThroughputSample throughput =
                        _throughputWindow.AddSample(elapsedSeconds, completed);
                    _timeline.Add(new PressureTestPoint(
                        elapsedSeconds,
                        throughput.RawTps,
                        throughput.SmoothedTps,
                        _activeUsers,
                        average,
                        p95,
                        p99,
                        completed,
                        recentLatency.AverageMilliseconds,
                        recentLatency.P95Milliseconds,
                        recentLatency.P99Milliseconds));
                    if (_timeline.Count > MaximumTimelinePoints) _timeline.RemoveAt(0);
                }

                return new PressureTestSnapshot
                {
                    StartedAt = _startedAt,
                    TotalRequests = totalRequests,
                    DurationLimitSeconds = durationLimitSeconds,
                    CompletedRequests = completed,
                    SuccessCount = _successCount,
                    FailureCount = _failureCount,
                    ActiveUsers = _activeUsers,
                    ElapsedSeconds = elapsedSeconds,
                    AverageMilliseconds = average,
                    Tps = tps,
                    PeakTps = _throughputWindow.PeakSmoothedTps,
                    SuccessRate = completed == 0 ? 0 : _successCount * 100d / completed,
                    P95Milliseconds = p95,
                    P99Milliseconds = p99,
                    MinimumMilliseconds = completed == 0 ? 0 : _minimum,
                    MaximumMilliseconds = _maximum,
                    IsRunning = isRunning,
                    IsCancelled = isCancelled,
                    RecordsRequestResponse = recordsRequestResponse,
                    PercentilesAreSampled = isRunning && _elapsed.Count > MaximumLivePercentileSamples,
                    ApproximatePayloadBytes = _payloadRetention.ApproximateBytes,
                    DiscardedPayloadCount = _payloadRetention.DiscardedCount,
                    Timeline = _timeline.ToArray(),
                    DistributionBuckets = _distribution.CreateBuckets(elapsedSeconds),
                    RequestResults = requestResults
                };
            }
        }
    }

    private sealed class PressureTestDistributionAccumulator(double slowRequestThresholdMilliseconds)
    {
        private const double RawBucketSeconds = 2;
        private const int MaximumDisplayBuckets = 60;
        private readonly List<MutableDistributionBucket> _buckets = [];

        public void Record(double completedAtSeconds, double elapsedMilliseconds, bool success)
        {
            int index = Math.Max(0, (int)Math.Floor(completedAtSeconds / RawBucketSeconds));
            while (_buckets.Count <= index) _buckets.Add(new MutableDistributionBucket());
            MutableDistributionBucket bucket = _buckets[index];
            bucket.TotalRequests++;
            if (!success) bucket.FailedRequests++;
            if (elapsedMilliseconds > slowRequestThresholdMilliseconds) bucket.SlowRequests++;
        }

        public IReadOnlyList<PressureTestDistributionBucket> CreateBuckets(double elapsedSeconds)
        {
            if (_buckets.Count == 0) return [];
            int elapsedBucketCount = Math.Max(
                1,
                (int)Math.Ceiling(Math.Max(0, elapsedSeconds) / RawBucketSeconds));
            int rawBucketCount = Math.Max(_buckets.Count, elapsedBucketCount);
            int groupSize = Math.Max(
                1,
                (int)Math.Ceiling(rawBucketCount / (double)MaximumDisplayBuckets));
            int displayBucketCount = (int)Math.Ceiling(rawBucketCount / (double)groupSize);
            var result = new PressureTestDistributionBucket[displayBucketCount];

            for (int displayIndex = 0; displayIndex < displayBucketCount; displayIndex++)
            {
                int startIndex = displayIndex * groupSize;
                int endIndex = Math.Min(rawBucketCount, startIndex + groupSize);
                int total = 0;
                int failures = 0;
                int slow = 0;
                for (int rawIndex = startIndex; rawIndex < Math.Min(endIndex, _buckets.Count); rawIndex++)
                {
                    MutableDistributionBucket bucket = _buckets[rawIndex];
                    total += bucket.TotalRequests;
                    failures += bucket.FailedRequests;
                    slow += bucket.SlowRequests;
                }

                result[displayIndex] = new PressureTestDistributionBucket(
                    startIndex * RawBucketSeconds,
                    Math.Min(endIndex * RawBucketSeconds, Math.Max(elapsedSeconds, .001)),
                    total,
                    failures,
                    slow);
            }

            return result;
        }

        private sealed class MutableDistributionBucket
        {
            public int TotalRequests { get; set; }
            public int FailedRequests { get; set; }
            public int SlowRequests { get; set; }
        }
    }
}
