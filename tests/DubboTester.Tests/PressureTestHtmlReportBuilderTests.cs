using DubboTester.Models;
using DubboTester.Services;
using Xunit;

namespace DubboTester.Tests;

public sealed class PressureTestHtmlReportBuilderTests
{
    [Fact]
    public void Build_CreatesSelfContainedEscapedReportAndFiltersSuccessfulDetails()
    {
        var configuration = new PressureTestConfiguration
        {
            UserCount = 2,
            IterationsPerUser = 3,
            UserStartIntervalMs = 125,
            StepStartIntervalSeconds = 3,
            StepUserCount = 2,
            RequestIntervalMs = 50,
            RecordRequestResponse = true,
            Settings = new TesterSettings { ZooKeeperAddress = "127.0.0.1:2181" },
            Invocation = new InvocationInput
            {
                ServiceName = "com.foo.<script>alert(1)</script>",
                MethodName = "check",
                JavaParameterTypes = "java.lang.Integer",
                RequestJson = "{{user}}"
            }
        };
        var snapshot = new PressureTestSnapshot
        {
            TotalRequests = 2,
            CompletedRequests = 2,
            SuccessCount = 1,
            FailureCount = 1,
            AverageMilliseconds = 20,
            Tps = 50,
            SuccessRate = 50,
            P95Milliseconds = 30,
            P99Milliseconds = 30,
            MinimumMilliseconds = 10,
            MaximumMilliseconds = 30,
            ElapsedSeconds = .04,
            RecordsRequestResponse = true,
            Timeline =
            [
                new PressureTestPoint(.02, 50, 10, 10, 10),
                new PressureTestPoint(.04, 50, 20, 30, 30)
            ],
            RequestResults =
            [
                new PressureTestRequestResult(999, 1, 10, .02, true, null),
                new PressureTestRequestResult(
                    2,
                    1,
                    30,
                    .04,
                    false,
                    "bad <value>",
                    Request: "{\"name\":\"<request>\"}",
                    Response: "<response>")
            ]
        };

        string html = PressureTestHtmlReportBuilder.Build(
            configuration,
            snapshot,
            DateTimeOffset.Parse("2026-09-30T10:00:00+08:00"),
            DateTimeOffset.Parse("2026-09-30T10:00:01+08:00"),
            includeAllRequests: false);

        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("com.foo.&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.DoesNotContain("com.foo.<script>", html);
        Assert.Contains("125 ms", html);
        Assert.Contains("3 s", html);
        Assert.Contains("阶梯用户数", html);
        Assert.Contains("bad &lt;value&gt;", html);
        Assert.Contains("&lt;request&gt;", html);
        Assert.Contains("&lt;response&gt;", html);
        Assert.DoesNotContain("<response>", html);
        Assert.DoesNotContain("<td>999</td>", html);
        Assert.Contains("<svg", html);
        Assert.DoesNotContain("https://", html);

        string allRequestsHtml = PressureTestHtmlReportBuilder.Build(
            configuration,
            snapshot,
            DateTimeOffset.Parse("2026-09-30T10:00:00+08:00"),
            DateTimeOffset.Parse("2026-09-30T10:00:01+08:00"),
            includeAllRequests: true);
        Assert.Contains("<td>999</td>", allRequestsHtml);
    }

    [Fact]
    public void Build_ExplainsPayloadsDiscardedByMemoryLimit()
    {
        var configuration = new PressureTestConfiguration
        {
            RecordRequestResponse = true,
            Settings = new TesterSettings(),
            Invocation = new InvocationInput
            {
                ServiceName = "com.foo.DemoService",
                MethodName = "echo"
            }
        };
        var snapshot = new PressureTestSnapshot
        {
            TotalRequests = 1,
            CompletedRequests = 1,
            SuccessCount = 1,
            RecordsRequestResponse = true,
            DiscardedPayloadCount = 1,
            RequestResults =
            [
                new PressureTestRequestResult(
                    1,
                    1,
                    1,
                    .001,
                    true,
                    null,
                    PayloadDiscarded: true)
            ]
        };

        string html = PressureTestHtmlReportBuilder.Build(
            configuration,
            snapshot,
            DateTimeOffset.Now,
            DateTimeOffset.Now,
            includeAllRequests: true);

        Assert.Contains("约 200 MB 内存上限清理", html);
        Assert.Contains("已因运行明细内存达到上限而清理", html);
    }

    [Fact]
    public void Build_DescribesDurationLimitMode()
    {
        var configuration = new PressureTestConfiguration
        {
            LimitMode = PressureTestLimitMode.Duration,
            DurationLimitSeconds = 90,
            Settings = new TesterSettings(),
            Invocation = new InvocationInput()
        };

        string html = PressureTestHtmlReportBuilder.Build(
            configuration,
            new PressureTestSnapshot { DurationLimitSeconds = 90 },
            DateTimeOffset.Now,
            DateTimeOffset.Now,
            includeAllRequests: false);

        Assert.Contains("时间上限", html);
        Assert.Contains("执行时长上限", html);
        Assert.Contains("90 s", html);
        Assert.DoesNotContain("每用户发送</span>", html);
    }
}
