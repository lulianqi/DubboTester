namespace DubboTester.Services;

internal readonly record struct PressureTestThroughputSample(
    double RawTps,
    double SmoothedTps);

internal sealed class PressureTestThroughputWindow(double windowSeconds = 5)
{
    private readonly List<CounterSample> _samples = [new(0, 0)];
    private double _previousElapsedSeconds;
    private int _previousCompletedRequests;

    public double PeakSmoothedTps { get; private set; }

    public PressureTestThroughputSample AddSample(
        double elapsedSeconds,
        int completedRequests)
    {
        elapsedSeconds = Math.Max(_previousElapsedSeconds, elapsedSeconds);
        completedRequests = Math.Max(_previousCompletedRequests, completedRequests);

        double rawSpan = elapsedSeconds - _previousElapsedSeconds;
        double rawTps = rawSpan <= 0
            ? 0
            : (completedRequests - _previousCompletedRequests) / rawSpan;

        _samples.Add(new CounterSample(elapsedSeconds, completedRequests));
        double cutoff = elapsedSeconds - Math.Max(windowSeconds, double.Epsilon);
        while (_samples.Count > 1 && _samples[1].ElapsedSeconds <= cutoff)
        {
            _samples.RemoveAt(0);
        }

        CounterSample baseline = _samples[0];
        double smoothSpan = elapsedSeconds - baseline.ElapsedSeconds;
        double smoothedTps = smoothSpan <= 0
            ? rawTps
            : (completedRequests - baseline.CompletedRequests) / smoothSpan;

        _previousElapsedSeconds = elapsedSeconds;
        _previousCompletedRequests = completedRequests;
        PeakSmoothedTps = Math.Max(PeakSmoothedTps, smoothedTps);
        return new PressureTestThroughputSample(rawTps, smoothedTps);
    }

    private readonly record struct CounterSample(
        double ElapsedSeconds,
        int CompletedRequests);
}
