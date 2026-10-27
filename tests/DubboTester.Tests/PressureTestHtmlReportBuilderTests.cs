using DubboNet.Clients;
using DubboTester.Models;
using DubboTester.Services;
using System.Text;
using System.Text.Json;
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
            Settings = new TesterSettings
            {
                ZooKeeperAddress = "127.0.0.1:2181",
                LoadBalanceMode = DubboClient.LoadBalanceMode.RoundRobin
            },
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
            PeakTps = 55,
            SuccessRate = 50,
            P95Milliseconds = 30,
            P99Milliseconds = 30,
            MinimumMilliseconds = 10,
            MaximumMilliseconds = 30,
            ElapsedSeconds = .04,
            RecordsRequestResponse = true,
            Timeline =
            [
                new PressureTestPoint(.02, 50, 45, 1, 10, 10, 10, 1, 8, 9, 10),
                new PressureTestPoint(.04, 50, 55, 2, 20, 30, 30, 2, 18, 28, 29)
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
            includeAllRequests: false,
            throughputSmoothingSeconds: 10,
            useRecentLatency: true);

        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("com.foo.&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.DoesNotContain("com.foo.<script>", html);
        Assert.Contains("125 ms", html);
        Assert.Contains("3 s", html);
        Assert.Contains("阶梯用户数", html);
        Assert.Contains("负载均衡模式", html);
        Assert.Contains("RoundRobin", html);
        Assert.Contains("最高 TPS", html);
        Assert.Contains("10 秒滑动平均", html);
        Assert.Contains("即时（最近 5 秒）", html);
        Assert.Contains("活跃用户", html);
        JsonElement filteredDetail = Assert.Single(ReadDetails(html));
        Assert.Equal("10:00:00", filteredDetail.GetProperty("t").GetString());
        Assert.Equal("bad <value>", filteredDetail.GetProperty("e").GetString());
        Assert.Equal("{\"name\":\"<request>\"}", filteredDetail.GetProperty("q").GetString());
        Assert.Equal("<response>", filteredDetail.GetProperty("r").GetString());
        Assert.DoesNotContain("<response>", html);
        Assert.DoesNotContain("\"u\":999", html);
        Assert.Contains("<svg", html);
        Assert.Equal(4, CountOccurrences(html, "<article class=\"card chart"));
        Assert.Contains(".charts{display:grid;grid-template-columns:1fr", html);
        Assert.Contains("id=\"filter-time-from\"", html);
        Assert.Contains("id=\"filter-error\"", html);
        Assert.Contains("id=\"filter-request\"", html);
        Assert.Contains("id=\"page-target\"", html);
        Assert.Contains("每页 100 条", html);
        Assert.DoesNotContain("https://", html);

        string allRequestsHtml = PressureTestHtmlReportBuilder.Build(
            configuration,
            snapshot,
            DateTimeOffset.Parse("2026-09-30T10:00:00+08:00"),
            DateTimeOffset.Parse("2026-09-30T10:00:01+08:00"),
            includeAllRequests: true);
        JsonElement[] allDetails = ReadDetails(allRequestsHtml);
        Assert.Equal(2, allDetails.Length);
        Assert.Equal(2, allDetails[0].GetProperty("u").GetInt32());
        Assert.Equal(999, allDetails[1].GetProperty("u").GetInt32());
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

        Assert.Contains("约 100 MB 内存上限清理", html);
        JsonElement detail = Assert.Single(ReadDetails(html));
        Assert.Equal("（已因运行明细内存达到上限而清理）", detail.GetProperty("q").GetString());
        Assert.Equal("（已因运行明细内存达到上限而清理）", detail.GetProperty("r").GetString());
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

    [Fact]
    public void Build_EmbedsEveryDetailBeyondThePreviousDisplayLimit()
    {
        const int resultCount = 20_001;
        var configuration = new PressureTestConfiguration
        {
            Settings = new TesterSettings(),
            Invocation = new InvocationInput()
        };
        PressureTestRequestResult[] results = Enumerable.Range(1, resultCount)
            .Select(index => new PressureTestRequestResult(1, index, 1, index / 1_000d, true, null))
            .ToArray();
        var snapshot = new PressureTestSnapshot
        {
            CompletedRequests = resultCount,
            SuccessCount = resultCount,
            ElapsedSeconds = resultCount / 1_000d,
            RequestResults = results
        };

        string html = PressureTestHtmlReportBuilder.Build(
            configuration,
            snapshot,
            DateTimeOffset.Parse("2026-09-30T10:00:00+08:00"),
            DateTimeOffset.Parse("2026-09-30T10:01:00+08:00"),
            includeAllRequests: true);

        Assert.Equal(resultCount, ReadDetails(html).Length);
        Assert.DoesNotContain("仅展示前", html);
    }

    [Fact]
    public async Task WriteAsync_EmitsLargeDetailSetsInBoundedChunks()
    {
        const int resultCount = 5_000;
        var configuration = new PressureTestConfiguration
        {
            RecordRequestResponse = true,
            Settings = new TesterSettings(),
            Invocation = new InvocationInput()
        };
        PressureTestRequestResult[] results = Enumerable.Range(1, resultCount)
            .Select(index => new PressureTestRequestResult(
                index % 10 + 1,
                index,
                12.5,
                index / 1_000d,
                true,
                null,
                Request: $"request-{index:D5}-{new string('q', 80)}",
                Response: $"response-{index:D5}-{new string('r', 80)}"))
            .ToArray();
        var snapshot = new PressureTestSnapshot
        {
            CompletedRequests = resultCount,
            SuccessCount = resultCount,
            RecordsRequestResponse = true,
            RequestResults = results
        };
        var writer = new MeasuringTextWriter();

        await PressureTestHtmlReportBuilder.WriteAsync(
            writer,
            configuration,
            snapshot,
            DateTimeOffset.Parse("2026-09-30T10:00:00+08:00"),
            DateTimeOffset.Parse("2026-09-30T10:01:00+08:00"),
            includeAllRequests: true);

        Assert.True(writer.WriteCount > 10);
        Assert.True(writer.TotalCharacters > 500_000);
        Assert.True(writer.MaximumWriteLength < 100_000);
    }

    private static JsonElement[] ReadDetails(string html)
    {
        const string startMarker = "<script id=\"detail-data\" type=\"application/json\">";
        const string endMarker = "</script>";
        int start = html.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0);
        start += startMarker.Length;
        int end = html.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start);
        using JsonDocument document = JsonDocument.Parse(html[start..end]);
        return document.RootElement.EnumerateArray().Select(element => element.Clone()).ToArray();
    }

    private static int CountOccurrences(string value, string search)
    {
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }
        return count;
    }

    private sealed class MeasuringTextWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public int WriteCount { get; private set; }
        public long TotalCharacters { get; private set; }
        public int MaximumWriteLength { get; private set; }

        public override Task WriteAsync(
            ReadOnlyMemory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteCount++;
            TotalCharacters += buffer.Length;
            MaximumWriteLength = Math.Max(MaximumWriteLength, buffer.Length);
            return Task.CompletedTask;
        }
    }
}
