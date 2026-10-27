namespace DubboTester.Models;

public enum BusinessAssertionJoin
{
    And,
    Or
}

public enum BusinessAssertionType
{
    ResponseContains = 0,
    JsonPath = 1,
    ResponseRegex = 2
}

public enum ResponseTextMatchOperator
{
    Contains,
    StartsWith,
    EndsWith,
    NotContains,
    Equals,
    NotEquals
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

public enum PressureTestLimitMode
{
    RequestCount,
    Duration
}

public enum PressureTestTcpReuseMode
{
    FullyShared,
    PerUser,
    SharedBy10Users,
    SharedBy20Users
}

public sealed class BusinessAssertionCondition
{
    public BusinessAssertionType Type { get; set; } = BusinessAssertionType.ResponseContains;
    public ResponseTextMatchOperator ResponseOperator { get; set; } = ResponseTextMatchOperator.Contains;
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
    public PressureTestTcpReuseMode TcpReuseMode { get; set; } = PressureTestTcpReuseMode.FullyShared;
    public PressureTestLimitMode LimitMode { get; set; } = PressureTestLimitMode.RequestCount;
    public int IterationsPerUser { get; set; } = 10;
    public int DurationLimitSeconds { get; set; } = 60;
    public int UserStartIntervalMs { get; set; }
    public int StepStartIntervalSeconds { get; set; }
    public int StepUserCount { get; set; }
    public int RequestIntervalMs { get; set; }
    public double SlowRequestThresholdMilliseconds { get; set; } = 500;
    public bool RecordRequestResponse { get; set; }
    public BusinessAssertionConfiguration BusinessAssertion { get; set; } = new();
    public required TesterSettings Settings { get; init; }
    public required InvocationInput Invocation { get; init; }

    public int TotalRequests => LimitMode == PressureTestLimitMode.RequestCount
        ? checked(UserCount * IterationsPerUser)
        : 0;
}

public sealed class PressureTestPageDraft
{
    public TesterSettings? Settings { get; set; }
    public InvocationInput Invocation { get; set; } = new();
    public int UserCount { get; set; } = 10;
    public PressureTestTcpReuseMode TcpReuseMode { get; set; } = PressureTestTcpReuseMode.FullyShared;
    public PressureTestLimitMode LimitMode { get; set; } = PressureTestLimitMode.RequestCount;
    public int IterationsPerUser { get; set; } = 10;
    public int DurationLimitSeconds { get; set; } = 60;
    public int UserStartIntervalMs { get; set; }
    public int StepStartIntervalSeconds { get; set; }
    public int StepUserCount { get; set; }
    public int RequestIntervalMs { get; set; }
    public double SlowRequestThresholdMilliseconds { get; set; } = 500;
    public bool RecordRequestResponse { get; set; }
    public BusinessAssertionConfiguration BusinessAssertion { get; set; } = new();
}

public sealed record PressureTestPoint(
    double ElapsedSeconds,
    double InstantTps,
    double SmoothedTps,
    int ActiveUsers,
    double AverageMilliseconds,
    double P95Milliseconds,
    double P99Milliseconds,
    int CompletedRequests = 0,
    double RecentAverageMilliseconds = 0,
    double RecentP95Milliseconds = 0,
    double RecentP99Milliseconds = 0,
    int SuccessfulRequests = 0,
    double SuccessfulInstantTps = 0,
    double RecentSuccessfulAverageMilliseconds = 0,
    int RecentSuccessfulRequests = 0,
    double SuccessfulAverageMilliseconds = 0,
    double SuccessfulP95Milliseconds = 0,
    double SuccessfulP99Milliseconds = 0,
    double RecentSuccessfulP95Milliseconds = 0,
    double RecentSuccessfulP99Milliseconds = 0);

public readonly record struct PressureTestRequestResult(
    int UserIndex,
    int IterationIndex,
    double ElapsedMilliseconds,
    double CompletedAtSeconds,
    bool Success,
    string? Error,
    string? Request = null,
    string? Response = null,
    bool PayloadDiscarded = false);

public sealed record PressureTestDistributionBucket(
    double StartedAtSeconds,
    double EndedAtSeconds,
    int TotalRequests,
    int FailedRequests,
    int SlowRequests)
{
    public double ErrorRate => TotalRequests == 0
        ? 0
        : FailedRequests * 100d / TotalRequests;

    public double SlowRate => TotalRequests == 0
        ? 0
        : SlowRequests * 100d / TotalRequests;
}

public sealed class PressureTestSnapshot
{
    public DateTimeOffset StartedAt { get; init; }
    public int TotalRequests { get; init; }
    public int DurationLimitSeconds { get; init; }
    public int CompletedRequests { get; init; }
    public int SuccessCount { get; init; }
    public int FailureCount { get; init; }
    public int ActiveUsers { get; init; }
    public double ElapsedSeconds { get; init; }
    public double AverageMilliseconds { get; init; }
    public double SuccessfulAverageMilliseconds { get; init; }
    public double Tps { get; init; }
    public double SuccessfulTps => ElapsedSeconds <= 0 ? 0 : SuccessCount / ElapsedSeconds;
    public double PeakTps { get; init; }
    public double SuccessRate { get; init; }
    public double P95Milliseconds { get; init; }
    public double P99Milliseconds { get; init; }
    public double MinimumMilliseconds { get; init; }
    public double MaximumMilliseconds { get; init; }
    public bool IsRunning { get; init; }
    public bool IsCancelled { get; init; }
    public bool RecordsRequestResponse { get; init; }
    public bool PercentilesAreSampled { get; init; }
    public long ApproximatePayloadBytes { get; init; }
    public int DiscardedPayloadCount { get; init; }
    public IReadOnlyList<PressureTestPoint> Timeline { get; init; } = [];
    public IReadOnlyList<PressureTestDistributionBucket> DistributionBuckets { get; init; } = [];
    public IReadOnlyList<PressureTestRequestResult> RequestResults { get; init; } = [];

    public double ProgressPercent => DurationLimitSeconds > 0
        ? Math.Clamp(ElapsedSeconds * 100d / DurationLimitSeconds, 0, 100)
        : TotalRequests == 0
            ? 0
            : Math.Clamp(CompletedRequests * 100d / TotalRequests, 0, 100);
}
