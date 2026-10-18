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
            RampUpIntervalMs = 125,
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
}
