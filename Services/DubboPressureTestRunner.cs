using System.Diagnostics;
using DubboTester.Models;

namespace DubboTester.Services;

public sealed class DubboPressureTestRunner(DubboSession session)
{
    private const int MaximumTotalRequests = 1_000_000;
    private const int MaximumIntervalMilliseconds = 3_600_000;

    public async Task<PressureTestSnapshot> RunAsync(
        PressureTestConfiguration configuration,
        IProgress<PressureTestSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Validate(configuration);
        string sample = PressureTestTemplateResolver.Resolve(
            configuration.Invocation.RequestJson,
            1,
            1);
        JsonArgumentParser.ParseArguments(sample);
        PressureTestBusinessAssertionEvaluator.Validate(configuration.BusinessAssertion);

        await session.PrepareForInvocationsAsync(
            configuration.Settings,
            configuration.Invocation,
            cancellationToken);

        var state = new RunState(
            configuration.TotalRequests,
            configuration.RecordRequestResponse);
        state.Start();
        Task[] workers = Enumerable.Range(1, configuration.UserCount)
            .Select(userIndex => RunUserAsync(
                configuration,
                userIndex,
                state,
                cancellationToken))
            .ToArray();
        Task allWorkers = Task.WhenAll(workers);
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

    private async Task RunUserAsync(
        PressureTestConfiguration configuration,
        int userIndex,
        RunState state,
        CancellationToken cancellationToken)
    {
        long rampUpDelay = (long)(userIndex - 1) * configuration.RampUpIntervalMs;
        if (rampUpDelay > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(rampUpDelay), cancellationToken);
        }

        state.ChangeActiveUsers(1);
        try
        {
            for (int iterationIndex = 1;
                 iterationIndex <= configuration.IterationsPerUser;
                 iterationIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (iterationIndex > 1 && configuration.RequestIntervalMs > 0)
                {
                    await Task.Delay(configuration.RequestIntervalMs, cancellationToken);
                }

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
                    InvocationOutput output = await session.InvokeAsync(
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
            progress.Report(state.CreateSnapshot(isRunning: true, isCancelled: false, addPoint: true));
            await Task.Delay(250);
        }
    }

    private static void Validate(PressureTestConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(configuration.Settings);
        ArgumentNullException.ThrowIfNull(configuration.Invocation);
        if (configuration.UserCount is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(configuration.UserCount), "并发用户数必须在 1 到 500 之间。");
        if (configuration.IterationsPerUser is < 1 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(configuration.IterationsPerUser), "每用户发送次数必须在 1 到 100000 之间。");
        if (configuration.RampUpIntervalMs is < 0 or > MaximumIntervalMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(configuration.RampUpIntervalMs), "阶梯启动间隔必须在 0 到 3600000 毫秒之间。");
        if (configuration.RequestIntervalMs is < 0 or > MaximumIntervalMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(configuration.RequestIntervalMs), "请求间隔必须在 0 到 3600000 毫秒之间。");
        if ((long)configuration.UserCount * configuration.IterationsPerUser > MaximumTotalRequests)
            throw new ArgumentOutOfRangeException(nameof(configuration.IterationsPerUser), $"单次压测最多允许 {MaximumTotalRequests:N0} 个请求。");
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

    private static string GetErrorMessage(Exception exception)
    {
        Exception current = exception;
        while (current.InnerException is not null) current = current.InnerException;
        return current.Message;
    }

    private sealed class RunState(int totalRequests, bool recordsRequestResponse)
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
        private int _successCount;
        private int _failureCount;
        private int _activeUsers;
        private int _lastPointCompleted;
        private double _lastPointSeconds;
        private double _elapsedTotal;
        private double _minimum = double.MaxValue;
        private double _maximum;

        public void Start() => _stopwatch.Start();
        public void Stop() => _stopwatch.Stop();

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
                _elapsed.Add(elapsed);
                _elapsedTotal += elapsed;
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

                _requestResults.Add(new PressureTestRequestResult(
                    userIndex,
                    iterationIndex,
                    elapsed,
                    _stopwatch.Elapsed.TotalSeconds,
                    success,
                    success ? null : error ?? "请求失败。",
                    recordsRequestResponse ? request : null,
                    recordsRequestResponse ? response : null));
            }
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

                if (addPoint && (completed != _lastPointCompleted || _timeline.Count == 0))
                {
                    double pointSpan = elapsedSeconds - _lastPointSeconds;
                    double instantTps = pointSpan <= 0
                        ? 0
                        : (completed - _lastPointCompleted) / pointSpan;
                    _timeline.Add(new PressureTestPoint(
                        elapsedSeconds,
                        instantTps,
                        average,
                        p95,
                        p99));
                    if (_timeline.Count > MaximumTimelinePoints) _timeline.RemoveAt(0);
                    _lastPointCompleted = completed;
                    _lastPointSeconds = elapsedSeconds;
                }

                return new PressureTestSnapshot
                {
                    TotalRequests = totalRequests,
                    CompletedRequests = completed,
                    SuccessCount = _successCount,
                    FailureCount = _failureCount,
                    ActiveUsers = _activeUsers,
                    ElapsedSeconds = elapsedSeconds,
                    AverageMilliseconds = average,
                    Tps = tps,
                    SuccessRate = completed == 0 ? 0 : _successCount * 100d / completed,
                    P95Milliseconds = p95,
                    P99Milliseconds = p99,
                    MinimumMilliseconds = completed == 0 ? 0 : _minimum,
                    MaximumMilliseconds = _maximum,
                    IsRunning = isRunning,
                    IsCancelled = isCancelled,
                    RecordsRequestResponse = recordsRequestResponse,
                    PercentilesAreSampled = isRunning && _elapsed.Count > MaximumLivePercentileSamples,
                    Timeline = _timeline.ToArray(),
                    RequestResults = requestResults
                };
            }
        }
    }
}
