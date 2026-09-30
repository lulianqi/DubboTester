namespace DubboTester.Models;

public enum BusinessAssertionJoin
{
    And,
    Or
}

public enum BusinessAssertionType
{
    ResponseContains,
    JsonPath
}

public enum BusinessAssertionOperator
{
    Exists,
    NotExists,
    Equals,
    NotEquals,
    Contains,
    NotContains,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual
}

public sealed class BusinessAssertionCondition
{
    public BusinessAssertionType Type { get; set; } = BusinessAssertionType.ResponseContains;
    public string JsonPath { get; set; } = "$.code";
    public BusinessAssertionOperator Operator { get; set; } = BusinessAssertionOperator.Equals;
    public string ExpectedValue { get; set; } = string.Empty;

    public BusinessAssertionCondition Clone() => (BusinessAssertionCondition)MemberwiseClone();
}

public sealed class BusinessAssertionConfiguration
{
    public BusinessAssertionJoin Join { get; set; } = BusinessAssertionJoin.And;
    public List<BusinessAssertionCondition> Conditions { get; set; } = [];

    public BusinessAssertionConfiguration Clone() => new()
    {
        Join = Join,
        Conditions = Conditions.Select(condition => condition.Clone()).ToList()
    };
}

public sealed class PressureTestConfiguration
{
    public int UserCount { get; set; } = 10;
    public int IterationsPerUser { get; set; } = 10;
    public int RampUpIntervalMs { get; set; }
    public int RequestIntervalMs { get; set; }
    public bool RecordRequestResponse { get; set; }
    public BusinessAssertionConfiguration BusinessAssertion { get; set; } = new();
    public required TesterSettings Settings { get; init; }
    public required InvocationInput Invocation { get; init; }

    public int TotalRequests => checked(UserCount * IterationsPerUser);
}

public sealed record PressureTestPoint(
    double ElapsedSeconds,
    double InstantTps,
    double AverageMilliseconds,
    double P95Milliseconds,
    double P99Milliseconds);

public readonly record struct PressureTestRequestResult(
    int UserIndex,
    int IterationIndex,
    double ElapsedMilliseconds,
    double CompletedAtSeconds,
    bool Success,
    string? Error,
    string? Request = null,
    string? Response = null);

public sealed class PressureTestSnapshot
{
    public int TotalRequests { get; init; }
    public int CompletedRequests { get; init; }
    public int SuccessCount { get; init; }
    public int FailureCount { get; init; }
    public int ActiveUsers { get; init; }
    public double ElapsedSeconds { get; init; }
    public double AverageMilliseconds { get; init; }
    public double Tps { get; init; }
    public double SuccessRate { get; init; }
    public double P95Milliseconds { get; init; }
    public double P99Milliseconds { get; init; }
    public double MinimumMilliseconds { get; init; }
    public double MaximumMilliseconds { get; init; }
    public bool IsRunning { get; init; }
    public bool IsCancelled { get; init; }
    public bool RecordsRequestResponse { get; init; }
    public bool PercentilesAreSampled { get; init; }
    public IReadOnlyList<PressureTestPoint> Timeline { get; init; } = [];
    public IReadOnlyList<PressureTestRequestResult> RequestResults { get; init; } = [];

    public double ProgressPercent => TotalRequests == 0
        ? 0
        : Math.Clamp(CompletedRequests * 100d / TotalRequests, 0, 100);
}
