using DubboTester.Models;

namespace DubboTester.Services;

public static class PressureTestChartMetrics
{
    public static double CalculateSmoothedTps(
        IReadOnlyList<PressureTestPoint> points,
        int pointIndex,
        double windowSeconds)
    {
        if (points.Count == 0 || pointIndex < 0 || pointIndex >= points.Count) return 0;

        PressureTestPoint current = points[pointIndex];
        double cutoff = current.ElapsedSeconds - Math.Max(windowSeconds, double.Epsilon);
        double baselineSeconds = 0;
        int baselineCompleted = 0;

        if (cutoff > 0)
        {
            int baselineIndex = FindLastIndexAtOrBefore(points, pointIndex - 1, cutoff);

            if (baselineIndex >= 0)
            {
                PressureTestPoint baseline = points[baselineIndex];
                baselineSeconds = baseline.ElapsedSeconds;
                baselineCompleted = baseline.CompletedRequests;
            }
            else if (points[0].ElapsedSeconds > 0 && current.ElapsedSeconds > windowSeconds)
            {
                PressureTestPoint baseline = points[0];
                baselineSeconds = baseline.ElapsedSeconds;
                baselineCompleted = baseline.CompletedRequests;
            }
        }

        double elapsed = current.ElapsedSeconds - baselineSeconds;
        return elapsed <= 0
            ? current.InstantTps
            : Math.Max(0, current.CompletedRequests - baselineCompleted) / elapsed;
    }

    public static double CalculatePeakSmoothedTps(
        IReadOnlyList<PressureTestPoint> points,
        double windowSeconds)
    {
        double peak = 0;
        for (int index = 0; index < points.Count; index++)
        {
            peak = Math.Max(peak, CalculateSmoothedTps(points, index, windowSeconds));
        }
        return peak;
    }

    public static double CalculateAdaptivePercentageMaximum(IEnumerable<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        double maximum = values
            .Where(double.IsFinite)
            .Select(value => Math.Clamp(value, 0, 100))
            .DefaultIfEmpty(0)
            .Max();
        if (maximum <= 0) return 1;

        double paddedMaximum = Math.Min(100, maximum * 1.12);
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(paddedMaximum)));
        double step = magnitude / 2;
        return Math.Min(100, Math.Ceiling(paddedMaximum / step) * step);
    }

    private static int FindLastIndexAtOrBefore(
        IReadOnlyList<PressureTestPoint> points,
        int maximumIndex,
        double elapsedSeconds)
    {
        int result = -1;
        int lower = 0;
        int upper = maximumIndex;
        while (lower <= upper)
        {
            int middle = lower + ((upper - lower) / 2);
            if (points[middle].ElapsedSeconds <= elapsedSeconds)
            {
                result = middle;
                lower = middle + 1;
            }
            else
            {
                upper = middle - 1;
            }
        }

        return result;
    }
}
