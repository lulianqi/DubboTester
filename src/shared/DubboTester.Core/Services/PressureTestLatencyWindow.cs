namespace DubboTester.Services;

internal readonly record struct PressureTestLatencyStatistics(
    double AverageMilliseconds,
    double P95Milliseconds,
    double P99Milliseconds,
    double SuccessfulAverageMilliseconds = 0,
    int SuccessfulRequests = 0,
    double SuccessfulP95Milliseconds = 0,
    double SuccessfulP99Milliseconds = 0);

internal sealed class PressureTestLatencyWindow(
    double windowSeconds = 5,
    int maximumPercentileSamples = 20_000)
{
    private readonly Queue<LatencySample> _samples = new();
    private double _elapsedTotal;
    private double _successfulElapsedTotal;
    private int _successfulRequests;

    public void Add(double completedAtSeconds, double elapsedMilliseconds, bool success = true)
    {
        double elapsed = Math.Max(0, elapsedMilliseconds);
        _samples.Enqueue(new LatencySample(Math.Max(0, completedAtSeconds), elapsed, success));
        _elapsedTotal += elapsed;
        if (success)
        {
            _successfulElapsedTotal += elapsed;
            _successfulRequests++;
        }
    }

    public PressureTestLatencyStatistics GetStatistics(double currentSeconds)
    {
        double cutoff = currentSeconds - Math.Max(windowSeconds, double.Epsilon);
        while (_samples.TryPeek(out LatencySample sample) && sample.CompletedAtSeconds < cutoff)
        {
            _samples.Dequeue();
            _elapsedTotal -= sample.ElapsedMilliseconds;
            if (sample.Success)
            {
                _successfulElapsedTotal -= sample.ElapsedMilliseconds;
                _successfulRequests--;
            }
        }

        if (_samples.Count == 0) return default;

        double[] percentileValues = BuildPercentileSample();
        Array.Sort(percentileValues);
        double[] successfulPercentileValues = _successfulRequests == _samples.Count
            ? percentileValues
            : BuildPercentileSample(successfulOnly: true);
        if (_successfulRequests != _samples.Count) Array.Sort(successfulPercentileValues);
        return new PressureTestLatencyStatistics(
            _elapsedTotal / _samples.Count,
            DubboPressureTestRunner.Percentile(percentileValues, .95),
            DubboPressureTestRunner.Percentile(percentileValues, .99),
            _successfulRequests == 0 ? 0 : _successfulElapsedTotal / _successfulRequests,
            _successfulRequests,
            DubboPressureTestRunner.Percentile(successfulPercentileValues, .95),
            DubboPressureTestRunner.Percentile(successfulPercentileValues, .99));
    }

    private double[] BuildPercentileSample(bool successfulOnly = false)
    {
        int count = successfulOnly ? _successfulRequests : _samples.Count;
        IEnumerable<LatencySample> samples = successfulOnly ? _samples.Where(sample => sample.Success) : _samples;
        if (count <= maximumPercentileSamples)
        {
            return samples.Select(sample => sample.ElapsedMilliseconds).ToArray();
        }

        var values = new double[maximumPercentileSamples];
        double step = count / (double)maximumPercentileSamples;
        int targetIndex = 0;
        int sourceIndex = 0;
        double nextSourceIndex = 0;
        foreach (LatencySample sample in samples)
        {
            if (sourceIndex >= Math.Floor(nextSourceIndex) && targetIndex < values.Length)
            {
                values[targetIndex++] = sample.ElapsedMilliseconds;
                nextSourceIndex += step;
            }
            sourceIndex++;
        }

        return targetIndex == values.Length ? values : values[..targetIndex];
    }

    private readonly record struct LatencySample(
        double CompletedAtSeconds,
        double ElapsedMilliseconds,
        bool Success);
}
