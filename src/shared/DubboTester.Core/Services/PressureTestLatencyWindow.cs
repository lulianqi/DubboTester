namespace DubboTester.Services;

internal readonly record struct PressureTestLatencyStatistics(
    double AverageMilliseconds,
    double P95Milliseconds,
    double P99Milliseconds);

internal sealed class PressureTestLatencyWindow(
    double windowSeconds = 5,
    int maximumPercentileSamples = 20_000)
{
    private readonly Queue<LatencySample> _samples = new();
    private double _elapsedTotal;

    public void Add(double completedAtSeconds, double elapsedMilliseconds)
    {
        double elapsed = Math.Max(0, elapsedMilliseconds);
        _samples.Enqueue(new LatencySample(Math.Max(0, completedAtSeconds), elapsed));
        _elapsedTotal += elapsed;
    }

    public PressureTestLatencyStatistics GetStatistics(double currentSeconds)
    {
        double cutoff = currentSeconds - Math.Max(windowSeconds, double.Epsilon);
        while (_samples.TryPeek(out LatencySample sample) && sample.CompletedAtSeconds < cutoff)
        {
            _elapsedTotal -= _samples.Dequeue().ElapsedMilliseconds;
        }

        if (_samples.Count == 0) return default;

        double[] percentileValues = BuildPercentileSample();
        Array.Sort(percentileValues);
        return new PressureTestLatencyStatistics(
            _elapsedTotal / _samples.Count,
            DubboPressureTestRunner.Percentile(percentileValues, .95),
            DubboPressureTestRunner.Percentile(percentileValues, .99));
    }

    private double[] BuildPercentileSample()
    {
        if (_samples.Count <= maximumPercentileSamples)
        {
            return _samples.Select(sample => sample.ElapsedMilliseconds).ToArray();
        }

        var values = new double[maximumPercentileSamples];
        double step = _samples.Count / (double)maximumPercentileSamples;
        int targetIndex = 0;
        int sourceIndex = 0;
        double nextSourceIndex = 0;
        foreach (LatencySample sample in _samples)
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
        double ElapsedMilliseconds);
}
