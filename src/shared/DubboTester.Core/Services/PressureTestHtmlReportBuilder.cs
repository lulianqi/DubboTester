using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using DubboTester.Models;

namespace DubboTester.Services;

public static class PressureTestHtmlReportBuilder
{
    public static string Build(
        PressureTestConfiguration configuration,
        PressureTestSnapshot snapshot,
        DateTimeOffset startedAt,
        DateTimeOffset finishedAt,
        bool includeAllRequests,
        int throughputSmoothingSeconds = 5,
        bool useRecentLatency = false)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        WriteAsync(
            writer,
            configuration,
            snapshot,
            startedAt,
            finishedAt,
            includeAllRequests,
            throughputSmoothingSeconds,
            useRecentLatency).GetAwaiter().GetResult();
        return writer.ToString();
    }

    public static async Task WriteAsync(
        TextWriter writer,
        PressureTestConfiguration configuration,
        PressureTestSnapshot snapshot,
        DateTimeOffset startedAt,
        DateTimeOffset finishedAt,
        bool includeAllRequests,
        int throughputSmoothingSeconds = 5,
        bool useRecentLatency = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(snapshot);

        int[] filteredIndexes = GetFilteredResultIndexes(snapshot.RequestResults, includeAllRequests);
        string title = $"Dubbo 压测报告 - {configuration.Invocation.ServiceName}#{configuration.Invocation.MethodName}";
        var html = new StringBuilder(64_000);
        html.Append("<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
            .Append("<title>").Append(Encode(title)).Append("</title><style>")
            .Append(Styles)
            .Append("</style></head><body><main>");

        html.Append("<header><div class=\"logo\">D</div><div><h1>")
            .Append(Encode(title))
            .Append("</h1><p>生成时间 ")
            .Append(Encode(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)))
            .Append("</p></div><span class=\"status ")
            .Append(snapshot.FailureCount > 0 ? "warn" : "ok")
            .Append("\">")
            .Append(snapshot.IsCancelled ? "已停止" : "已完成")
            .Append("</span></header>");

        html.Append("<section class=\"card config\"><h2>压测配置</h2><div class=\"config-grid\">")
            .Append(Item("调用方式", configuration.Invocation.Route == InvocationRoute.Registry ? "注册中心" : "直连 Provider"))
            .Append(configuration.Invocation.Route == InvocationRoute.Registry
                ? Item("注册中心类型", configuration.Settings.RegistryDisplayName)
                : string.Empty)
            .Append(Item("连接地址", configuration.Invocation.Route == InvocationRoute.Registry
                ? configuration.Settings.RegistryAddress
                : $"{configuration.Invocation.Host}:{configuration.Invocation.Port}"))
            .Append(Item("服务", configuration.Invocation.ServiceName))
            .Append(Item("方法", configuration.Invocation.MethodName))
            .Append(Item("Java 参数类型", configuration.Invocation.JavaParameterTypes))
            .Append(Item("版本 / 分组", $"{EmptyAsDash(configuration.Invocation.Version)} / {EmptyAsDash(configuration.Invocation.Group)}"))
            .Append(Item("并发用户", configuration.UserCount.ToString("N0", CultureInfo.InvariantCulture)))
            .Append(Item("TCP 复用模式", TcpReuseModeLabel(configuration.TcpReuseMode)))
            .Append(Item("Dubbo client 数", DubboPressureTestRunner.GetClientCount(
                configuration.UserCount,
                configuration.TcpReuseMode).ToString("N0", CultureInfo.InvariantCulture)))
            .Append(Item("终止方式", configuration.LimitMode == PressureTestLimitMode.RequestCount ? "次数上限" : "时间上限"))
            .Append(configuration.LimitMode == PressureTestLimitMode.RequestCount
                ? Item("每用户发送", configuration.IterationsPerUser.ToString("N0", CultureInfo.InvariantCulture))
                : Item("执行时长上限", $"{configuration.DurationLimitSeconds:N0} s"))
            .Append(Item("用户启动间隔", $"{configuration.UserStartIntervalMs:N0} ms"))
            .Append(Item("阶梯启动间隔", $"{configuration.StepStartIntervalSeconds:N0} s"))
            .Append(Item("阶梯用户数", configuration.StepUserCount == 0
                ? "全部用户"
                : configuration.StepUserCount.ToString("N0", CultureInfo.InvariantCulture)))
            .Append(Item("请求间隔", $"{configuration.RequestIntervalMs:N0} ms"))
            .Append(Item("慢请求阈值", $"> {configuration.SlowRequestThresholdMilliseconds:N0} ms"))
            .Append(Item("记录请求与响应", configuration.RecordRequestResponse ? "开启" : "关闭"))
            .Append(Item("业务断言", PressureTestBusinessAssertionEvaluator.Describe(configuration.BusinessAssertion)))
            .Append(Item("开始时间", startedAt.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)))
            .Append(Item("结束时间", finishedAt.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)))
            .Append("</div><h3>请求参数模板</h3><pre>")
            .Append(Encode(configuration.Invocation.RequestJson))
            .Append("</pre></section>");

        double peakTps = PressureTestChartMetrics.CalculatePeakSmoothedTps(
            snapshot.Timeline,
            throughputSmoothingSeconds);
        html.Append("<section class=\"metrics\">")
            .Append(Metric("平均响应", $"{snapshot.AverageMilliseconds:F2} ms", "所有已完成请求"))
            .Append(Metric("TPS", snapshot.Tps.ToString("F2", CultureInfo.InvariantCulture), $"最高 TPS {peakTps:F2}（{throughputSmoothingSeconds} 秒滑动平均峰值）"))
            .Append(Metric("成功率", $"{snapshot.SuccessRate:F2}%", $"{snapshot.SuccessCount:N0} 成功 / {snapshot.FailureCount:N0} 失败"))
            .Append(Metric("P95", $"{snapshot.P95Milliseconds:F2} ms", "最终精确分位数"))
            .Append(Metric("P99", $"{snapshot.P99Milliseconds:F2} ms", "最终精确分位数"))
            .Append(Metric("运行时间", $"{snapshot.ElapsedSeconds:F2} s", $"{snapshot.CompletedRequests:N0} 次已完成"))
            .Append("</section>");

        html.Append(InteractiveCharts(
                throughputSmoothingSeconds,
                useRecentLatency,
                configuration.SlowRequestThresholdMilliseconds))
            .Append("<script id=\"chart-data\" type=\"application/json\">")
            .Append(BuildChartData(
                snapshot,
                startedAt,
                configuration.UserCount,
                throughputSmoothingSeconds,
                useRecentLatency,
                configuration.SlowRequestThresholdMilliseconds,
                includeAllRequests && filteredIndexes.Length == snapshot.RequestResults.Count))
            .Append("</script>");

        html.Append("<section class=\"card details\"><div class=\"section-title\"><div><h2>运行明细</h2><p>")
            .Append(includeAllRequests ? "全部请求" : "仅失败请求")
            .Append(snapshot.RecordsRequestResponse ? "；包含当前保留的请求与响应" : "；未记录请求与响应正文");
        if (snapshot.DiscardedPayloadCount > 0)
        {
            html.Append("；已因约 100 MB 内存上限清理 ")
                .Append(snapshot.DiscardedPayloadCount.ToString("N0", CultureInfo.InvariantCulture))
                .Append(" 条较旧正文（失败请求优先保留）");
        }
        html.Append("</p></div><b>Min ")
            .Append(snapshot.MinimumMilliseconds.ToString("F2", CultureInfo.InvariantCulture))
            .Append(" ms · Max ")
            .Append(snapshot.MaximumMilliseconds.ToString("F2", CultureInfo.InvariantCulture))
            .Append(" ms</b></div>");

        if (filteredIndexes.Length == 0)
        {
            html.Append("<div class=\"empty\">")
                .Append(includeAllRequests ? "暂无请求结果" : "暂无失败请求")
                .Append("</div>");
        }
        else
        {
            AppendDetailFilters(html, snapshot.RecordsRequestResponse);
            html.Append("<div class=\"detail-result-meta\"><span id=\"detail-count\"></span><span>每页 100 条</span></div>")
                .Append("<div class=\"table-wrap\"><table><thead><tr><th>#</th><th>用户</th><th>发送序号</th><th>完成时刻</th><th>耗时</th><th>状态</th><th>错误</th>");
            if (snapshot.RecordsRequestResponse)
            {
                html.Append("<th>请求与响应</th>");
            }
            html.Append("</tr></thead><tbody id=\"detail-body\"></tbody></table></div>")
                .Append("<div class=\"detail-pagination\"><button id=\"page-first\">«</button><button id=\"page-prev\">‹</button><b id=\"page-label\"></b><button id=\"page-next\">›</button><button id=\"page-last\">»</button><span>跳至 <input id=\"page-target\" type=\"number\" min=\"1\" step=\"1\"><button id=\"page-go\">跳转</button></span></div>");
        }

        await writer.WriteAsync(html.ToString().AsMemory(), cancellationToken).ConfigureAwait(false);
        if (filteredIndexes.Length > 0)
        {
            await WriteDetailDataAsync(
                writer,
                snapshot.RequestResults,
                filteredIndexes,
                startedAt,
                snapshot.RecordsRequestResponse,
                cancellationToken).ConfigureAwait(false);
        }

        string footer = "</section><footer>由 Dubbo Tester 生成 · 报告包含本次压测的全部轻量级运行明细</footer></main>"
            + ChartScript
            + (filteredIndexes.Length > 0 ? DetailScript : string.Empty)
            + "</body></html>";
        await writer.WriteAsync(footer.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private static int[] GetFilteredResultIndexes(
        IReadOnlyList<PressureTestRequestResult> results,
        bool includeAllRequests)
    {
        int[] indexes = Enumerable.Range(0, results.Count)
            .Where(index => includeAllRequests || !results[index].Success)
            .ToArray();
        Array.Sort(indexes, (leftIndex, rightIndex) =>
        {
            PressureTestRequestResult left = results[leftIndex];
            PressureTestRequestResult right = results[rightIndex];
            int comparison = right.CompletedAtSeconds.CompareTo(left.CompletedAtSeconds);
            if (comparison != 0) return comparison;
            comparison = right.IterationIndex.CompareTo(left.IterationIndex);
            return comparison != 0
                ? comparison
                : right.UserIndex.CompareTo(left.UserIndex);
        });
        return indexes;
    }

    private static string TcpReuseModeLabel(PressureTestTcpReuseMode mode) => mode switch
    {
        PressureTestTcpReuseMode.FullyShared => "完全复用（全部用户共用一个 client）",
        PressureTestTcpReuseMode.PerUser => "不复用（每个用户一个 client）",
        PressureTestTcpReuseMode.SharedBy10Users => "每 10 个用户复用一个 client",
        PressureTestTcpReuseMode.SharedBy20Users => "每 20 个用户复用一个 client",
        _ => mode.ToString()
    };

    private static string InteractiveCharts(
        int throughputSmoothingSeconds,
        bool useRecentLatency,
        double slowRequestThresholdMilliseconds)
    {
        int smoothingSeconds = throughputSmoothingSeconds is 1 or 5 or 10 or 30
            ? throughputSmoothingSeconds
            : 5;
        string latencyMode = useRecentLatency ? "recent" : "cumulative";
        return $$"""
            <section class="charts">
              <article class="card chart" data-chart="throughput">
                <div class="chart-heading"><div><h2>吞吐趋势</h2><p>500 ms 采样</p></div><button class="fullscreen-button" type="button" data-fullscreen aria-label="最大化吞吐趋势">⛶</button></div>
                <div class="chart-toolbar">
                  <label class="compact-control"><select id="throughput-smoothing" aria-label="TPS 平滑时间"><option value="1"{{(smoothingSeconds == 1 ? " selected" : string.Empty)}}>1 秒平滑</option><option value="5"{{(smoothingSeconds == 5 ? " selected" : string.Empty)}}>5 秒平滑</option><option value="10"{{(smoothingSeconds == 10 ? " selected" : string.Empty)}}>10 秒平滑</option><option value="30"{{(smoothingSeconds == 30 ? " selected" : string.Empty)}}>30 秒平滑</option></select></label>
                  <span class="legend"><i class="blue-dot"></i><span id="throughput-legend">平滑 TPS</span></span><span class="legend"><i class="purple-dot"></i>活跃用户</span>
                  <span class="zoom-control"><button type="button" data-zoom-out aria-label="缩小时间轴">−</button><b data-zoom-label>1x</b><button type="button" data-zoom-in aria-label="放大时间轴">＋</button></span>
                </div>
                <div class="chart-viewport"><svg role="img" aria-label="吞吐趋势图"></svg><div class="chart-tooltip" hidden></div></div>
              </article>
              <article class="card chart" data-chart="latency">
                <div class="chart-heading"><div><h2>响应时间趋势</h2><p>平均 / P95 / P99（ms）</p></div><button class="fullscreen-button" type="button" data-fullscreen aria-label="最大化响应时间趋势">⛶</button></div>
                <div class="chart-toolbar">
                  <label class="compact-control"><select id="latency-mode" aria-label="响应时间统计方式"><option value="cumulative"{{(latencyMode == "cumulative" ? " selected" : string.Empty)}}>累计统计</option><option value="recent"{{(latencyMode == "recent" ? " selected" : string.Empty)}}>即时（最近 5 秒）</option></select></label>
                  <span class="legend"><i class="green-dot"></i>平均</span><span class="legend"><i class="orange-dot"></i>P95</span><span class="legend"><i class="red-dot"></i>P99</span><span class="legend"><i class="purple-dot"></i>活跃用户</span>
                  <span class="zoom-control"><button type="button" data-zoom-out aria-label="缩小时间轴">−</button><b data-zoom-label>1x</b><button type="button" data-zoom-in aria-label="放大时间轴">＋</button></span>
                </div>
                <div class="chart-viewport"><svg role="img" aria-label="响应时间趋势图"></svg><div class="chart-tooltip" hidden></div></div>
              </article>
              <article class="card chart" data-chart="errors">
                <div class="chart-heading"><div><h2>错误请求分布</h2><p>柱状图：错误请求数 · 折线：错误率</p></div><button class="fullscreen-button" type="button" data-fullscreen aria-label="最大化错误请求分布">⛶</button></div>
                <div class="chart-toolbar"><span class="legend"><i class="error-bar-dot"></i>错误请求数</span><span class="legend"><i class="error-rate-dot"></i>错误率</span><span class="zoom-control"><button type="button" data-zoom-out aria-label="缩小时间轴">−</button><b data-zoom-label>1x</b><button type="button" data-zoom-in aria-label="放大时间轴">＋</button></span></div>
                <div class="chart-viewport"><svg role="img" aria-label="错误请求分布图"></svg><div class="chart-tooltip" hidden></div></div>
              </article>
              <article class="card chart" data-chart="slow">
                <div class="chart-heading"><div><h2>慢请求分布</h2><p>柱状图：慢请求数 · 折线：慢请求率</p></div><button class="fullscreen-button" type="button" data-fullscreen aria-label="最大化慢请求分布">⛶</button></div>
                <div class="chart-toolbar"><label class="threshold-control">耗时大于 <input id="slow-threshold" type="number" min="0" step="1" value="{{slowRequestThresholdMilliseconds.ToString("F0", CultureInfo.InvariantCulture)}}" aria-label="慢请求耗时阈值"> ms</label><span class="legend"><i class="slow-bar-dot"></i>慢请求数</span><span class="legend"><i class="slow-rate-dot"></i>慢请求率</span><span class="zoom-control"><button type="button" data-zoom-out aria-label="缩小时间轴">−</button><b data-zoom-label>1x</b><button type="button" data-zoom-in aria-label="放大时间轴">＋</button></span></div>
                <div class="chart-viewport"><svg role="img" aria-label="慢请求分布图"></svg><div class="chart-tooltip" hidden></div></div>
              </article>
            </section>
            """;
    }

    private static string BuildChartData(
        PressureTestSnapshot snapshot,
        DateTimeOffset startedAt,
        int configuredUserCount,
        int throughputSmoothingSeconds,
        bool useRecentLatency,
        double slowRequestThresholdMilliseconds,
        bool hasAllResults)
    {
        IReadOnlyList<PressureTestDistributionBucket> initialBuckets =
            PressureTestDistributionBuilder.Build(
                snapshot.RequestResults,
                snapshot.ElapsedSeconds,
                slowRequestThresholdMilliseconds);
        return JsonSerializer.Serialize(new
        {
            startedAt = startedAt.ToUnixTimeMilliseconds(),
            elapsedSeconds = snapshot.ElapsedSeconds,
            configuredUserCount,
            throughputSmoothingSeconds,
            useRecentLatency,
            slowRequestThresholdMilliseconds,
            hasAllResults,
            timeline = snapshot.Timeline.Select(point => new object[]
            {
                point.ElapsedSeconds,
                point.InstantTps,
                point.CompletedRequests,
                point.ActiveUsers,
                point.AverageMilliseconds,
                point.P95Milliseconds,
                point.P99Milliseconds,
                point.RecentAverageMilliseconds,
                point.RecentP95Milliseconds,
                point.RecentP99Milliseconds
            }),
            initialBuckets = initialBuckets.Select(bucket => new object[]
            {
                bucket.StartedAtSeconds,
                bucket.EndedAtSeconds,
                bucket.TotalRequests,
                bucket.FailedRequests,
                bucket.SlowRequests
            })
        });
    }

    private static void AppendDetailFilters(StringBuilder html, bool includesPayload)
    {
        html.Append("<div class=\"detail-filters\">")
            .Append("<label>完成时刻从<input id=\"filter-time-from\" type=\"time\" step=\"1\"></label>")
            .Append("<label>至<input id=\"filter-time-to\" type=\"time\" step=\"1\"></label>")
            .Append("<label>用户最小值<input id=\"filter-user-min\" type=\"number\" min=\"1\" step=\"1\"></label>")
            .Append("<label>用户最大值<input id=\"filter-user-max\" type=\"number\" min=\"1\" step=\"1\"></label>")
            .Append("<label>发送序号最小值<input id=\"filter-iteration-min\" type=\"number\" min=\"1\" step=\"1\"></label>")
            .Append("<label>发送序号最大值<input id=\"filter-iteration-max\" type=\"number\" min=\"1\" step=\"1\"></label>")
            .Append("<label>耗时最小值（ms）<input id=\"filter-ms-min\" type=\"number\" min=\"0\" step=\"0.01\"></label>")
            .Append("<label>耗时最大值（ms）<input id=\"filter-ms-max\" type=\"number\" min=\"0\" step=\"0.01\"></label>")
            .Append("<label>状态<select id=\"filter-status\"><option value=\"all\">全部</option><option value=\"success\">成功</option><option value=\"failure\">失败</option></select></label>")
            .Append("<label>错误信息包含<input id=\"filter-error\" type=\"search\" placeholder=\"输入关键词\"></label>");
        if (includesPayload)
        {
            html.Append("<label>请求内容包含<input id=\"filter-request\" type=\"search\" placeholder=\"输入关键词\"></label>")
                .Append("<label>响应内容包含<input id=\"filter-response\" type=\"search\" placeholder=\"输入关键词\"></label>");
        }
        html.Append("<div class=\"detail-filter-actions\"><button id=\"filter-clear\">清除</button><button id=\"filter-apply\" class=\"primary\">应用筛选</button></div></div>");
    }

    private static async Task WriteDetailDataAsync(
        TextWriter writer,
        IReadOnlyList<PressureTestRequestResult> results,
        IReadOnlyList<int> resultIndexes,
        DateTimeOffset startedAt,
        bool includesPayload,
        CancellationToken cancellationToken)
    {
        await writer.WriteAsync(
            "<script id=\"detail-data\" type=\"application/json\">[".AsMemory(),
            cancellationToken).ConfigureAwait(false);
        var buffer = new StringBuilder(64 * 1024);
        for (int index = 0; index < resultIndexes.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index > 0) buffer.Append(',');
            PressureTestRequestResult item = results[resultIndexes[index]];
            buffer.Append("{\"u\":").Append(item.UserIndex)
                .Append(",\"i\":").Append(item.IterationIndex)
                .Append(",\"s\":").Append(item.CompletedAtSeconds.ToString("F3", CultureInfo.InvariantCulture))
                .Append(",\"t\":");
            AppendJsonString(buffer, startedAt.AddSeconds(Math.Max(0, item.CompletedAtSeconds)).ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            buffer.Append(",\"ms\":").Append(item.ElapsedMilliseconds.ToString("F3", CultureInfo.InvariantCulture))
                .Append(",\"ok\":").Append(item.Success ? "true" : "false")
                .Append(",\"e\":");
            AppendJsonString(buffer, item.Error ?? string.Empty);
            if (includesPayload)
            {
                buffer.Append(",\"q\":");
                AppendJsonString(buffer, RequestText(item));
                buffer.Append(",\"r\":");
                AppendJsonString(buffer, ResponseText(item));
            }
            buffer.Append('}');

            if (buffer.Length >= 64 * 1024)
            {
                await writer.WriteAsync(buffer.ToString().AsMemory(), cancellationToken).ConfigureAwait(false);
                buffer.Clear();
            }
        }
        if (buffer.Length > 0)
        {
            await writer.WriteAsync(buffer.ToString().AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        await writer.WriteAsync("]</script>".AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private static void AppendJsonString(StringBuilder html, string value) =>
        html.Append(JsonSerializer.Serialize(value));

    private static string Item(string label, string? value) =>
        $"<div><span>{Encode(label)}</span><b>{Encode(EmptyAsDash(value))}</b></div>";

    private static string Metric(string label, string value, string note) =>
        $"<article><span>{Encode(label)}</span><b>{Encode(value)}</b><small>{Encode(note)}</small></article>";

    private static string EmptyAsDash(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
    private static string RequestText(PressureTestRequestResult result) =>
        result.PayloadDiscarded
            ? "（已因运行明细内存达到上限而清理）"
            : result.Request ?? "（未记录请求内容）";
    private static string ResponseText(PressureTestRequestResult result) =>
        result.PayloadDiscarded
            ? "（已因运行明细内存达到上限而清理）"
            : !string.IsNullOrEmpty(result.Response)
            ? result.Response
            : result.Success
                ? "（空响应）"
                : result.Error ?? "（无响应内容）";
    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private const string ChartScript = """
        <script>
        (() => {
          'use strict';
          const chartNode = document.getElementById('chart-data');
          if (!chartNode) return;

          const report = JSON.parse(chartNode.textContent);
          const detailNode = document.getElementById('detail-data');
          const allRows = detailNode ? JSON.parse(detailNode.textContent) : [];
          window.__pressureReportRows = allRows;
          const cards = Object.fromEntries(
            [...document.querySelectorAll('[data-chart]')].map(card => [card.dataset.chart, card]));
          const zooms = { throughput: 1, latency: 1, errors: 1, slow: 1 };
          const minimumZoom = 1;
          const maximumZoom = 6;
          const zoomStep = .5;
          const svgNamespace = 'http://www.w3.org/2000/svg';

          const clamp = (value, minimum, maximum) => Math.min(maximum, Math.max(minimum, value));
          const finite = value => Number.isFinite(Number(value)) ? Number(value) : 0;
          const formatNumber = (value, maximumFractionDigits = 2) => finite(value).toLocaleString('zh-CN', { maximumFractionDigits });
          const formatCount = value => `${Math.max(0, Math.round(finite(value))).toLocaleString('zh-CN')} 次`;
          const formatMilliseconds = value => `${formatNumber(value, 2)} ms`;
          const formatPercent = value => `${formatNumber(value, finite(value) < 1 ? 3 : 2)}%`;
          const formatElapsed = value => `${formatNumber(value, 2)} s`;
          const clockFormatter = new Intl.DateTimeFormat('zh-CN', {
            timeZone: 'Asia/Shanghai', hour12: false, hour: '2-digit', minute: '2-digit', second: '2-digit'
          });
          const formatClock = elapsedSeconds => clockFormatter.format(
            new Date(report.startedAt + Math.max(0, finite(elapsedSeconds)) * 1000));

          function dimensions(card, dualAxis) {
            const viewport = card.querySelector('.chart-viewport');
            const zoom = zooms[card.dataset.chart];
            const width = Math.max(820, Math.round(viewport.clientWidth * zoom));
            const height = Math.max(360, viewport.clientHeight || 380);
            return {
              width, height, left: 72, right: dualAxis ? 68 : 26, top: 18, bottom: 46,
              get plotWidth() { return this.width - this.left - this.right; },
              get plotHeight() { return this.height - this.top - this.bottom; },
              get plotRight() { return this.width - this.right; },
              get plotBottom() { return this.height - this.bottom; }
            };
          }

          function setSvgSize(svg, size) {
            svg.setAttribute('viewBox', `0 0 ${size.width} ${size.height}`);
            svg.setAttribute('width', String(size.width));
            svg.setAttribute('height', String(size.height));
            svg.style.width = `${size.width}px`;
            svg.style.height = `${size.height}px`;
          }

          function xAt(index, count, size) {
            return count <= 1
              ? size.left + size.plotWidth / 2
              : size.left + index * size.plotWidth / (count - 1);
          }

          function yAt(value, maximum, size) {
            return size.plotBottom - clamp(finite(value) / Math.max(1e-9, maximum), 0, 1) * size.plotHeight;
          }

          function pathFor(values, maximum, size) {
            if (!values.length) return '';
            return values.map((value, index) =>
              `${index ? 'L' : 'M'}${xAt(index, values.length, size).toFixed(2)},${yAt(value, maximum, size).toFixed(2)}`
            ).join(' ');
          }

          function bucketPathFor(values, maximum, size) {
            if (!values.length) return '';
            return values.map((value, index) => {
              const x = size.left + (index + .5) * size.plotWidth / values.length;
              return `${index ? 'L' : 'M'}${x.toFixed(2)},${yAt(value, maximum, size).toFixed(2)}`;
            }).join(' ');
          }

          function axisMarkup(size, duration, leftMaximum, leftFormatter, rightMaximum, rightFormatter) {
            let markup = '';
            for (let index = 0; index <= 4; index++) {
              const ratio = index / 4;
              const y = size.top + ratio * size.plotHeight;
              const leftValue = leftMaximum * (1 - ratio);
              markup += `<line class="chart-grid-line" x1="${size.left}" y1="${y}" x2="${size.plotRight}" y2="${y}"></line>`;
              markup += `<text class="axis-label" x="${size.left - 10}" y="${y + 4}" text-anchor="end">${leftFormatter(leftValue)}</text>`;
              if (rightFormatter) {
                const rightValue = rightMaximum * (1 - ratio);
                markup += `<text class="axis-label" x="${size.width - 4}" y="${y + 4}" text-anchor="end">${rightFormatter(rightValue)}</text>`;
              }
            }
            for (let index = 0; index <= 4; index++) {
              const ratio = index / 4;
              const x = size.left + ratio * size.plotWidth;
              markup += `<line class="axis-tick" x1="${x}" y1="${size.plotBottom}" x2="${x}" y2="${size.plotBottom + 5}"></line>`;
              markup += `<text class="axis-label" x="${x}" y="${size.height - 12}" text-anchor="middle">${formatClock(duration * ratio)}</text>`;
            }
            markup += `<line class="axis-line" x1="${size.left}" y1="${size.plotBottom}" x2="${size.plotRight}" y2="${size.plotBottom}"></line>`;
            return markup;
          }

          function emptyMarkup(size) {
            return `<text class="chart-empty" x="${size.width / 2}" y="${size.height / 2}" text-anchor="middle">暂无图表数据</text>`;
          }

          function createHoverLayer(svg) {
            const layer = document.createElementNS(svgNamespace, 'g');
            layer.classList.add('chart-hover-layer');
            svg.appendChild(layer);
            return layer;
          }

          function showTooltip(card, event, title, rows) {
            const viewport = card.querySelector('.chart-viewport');
            const tooltip = card.querySelector('.chart-tooltip');
            tooltip.replaceChildren();
            const heading = document.createElement('b');
            heading.textContent = title;
            tooltip.appendChild(heading);
            rows.forEach(([label, value, color]) => {
              const row = document.createElement('div');
              const name = document.createElement('span');
              if (color) {
                const dot = document.createElement('i');
                dot.style.background = color;
                name.appendChild(dot);
              }
              name.append(label);
              const output = document.createElement('strong');
              output.textContent = value;
              row.append(name, output);
              tooltip.appendChild(row);
            });
            tooltip.hidden = false;
            const viewportRect = viewport.getBoundingClientRect();
            const tooltipRect = tooltip.getBoundingClientRect();
            const pointerX = event.clientX - viewportRect.left + viewport.scrollLeft;
            const pointerY = event.clientY - viewportRect.top;
            const left = clamp(pointerX + 14, viewport.scrollLeft + 8, viewport.scrollLeft + viewport.clientWidth - tooltipRect.width - 8);
            const top = clamp(pointerY - tooltipRect.height / 2, 8, viewport.clientHeight - tooltipRect.height - 8);
            tooltip.style.left = `${left}px`;
            tooltip.style.top = `${top}px`;
          }

          function hideTooltip(card) {
            const tooltip = card.querySelector('.chart-tooltip');
            tooltip.hidden = true;
            card.querySelector('.chart-hover-layer')?.replaceChildren();
          }

          function bindLineHover(card, size, pointCount, valuesAt, tooltipAt) {
            const svg = card.querySelector('svg');
            const layer = createHoverLayer(svg);
            const hit = document.createElementNS(svgNamespace, 'rect');
            hit.setAttribute('class', 'chart-hit-area');
            hit.setAttribute('x', String(size.left));
            hit.setAttribute('y', String(size.top));
            hit.setAttribute('width', String(size.plotWidth));
            hit.setAttribute('height', String(size.plotHeight));
            svg.appendChild(hit);
            hit.addEventListener('pointermove', event => {
              if (!pointCount) return;
              const rect = svg.getBoundingClientRect();
              const svgX = (event.clientX - rect.left) * size.width / rect.width;
              const ratio = clamp((svgX - size.left) / size.plotWidth, 0, 1);
              const index = Math.round(ratio * (pointCount - 1));
              const x = xAt(index, pointCount, size);
              const values = valuesAt(index);
              layer.innerHTML = `<line class="hover-line" x1="${x}" y1="${size.top}" x2="${x}" y2="${size.plotBottom}"></line>`
                + values.map(item => `<circle class="hover-point" cx="${x}" cy="${item.y}" r="4" style="fill:${item.color}"></circle>`).join('');
              tooltipAt(index, event);
            });
            hit.addEventListener('pointerleave', () => hideTooltip(card));
          }

          function smoothedTps(points, index, windowSeconds) {
            if (!points.length || index < 0 || index >= points.length) return 0;
            const current = points[index];
            const cutoff = current[0] - Math.max(windowSeconds, Number.EPSILON);
            let baselineSeconds = 0;
            let baselineCompleted = 0;
            if (cutoff > 0) {
              let lower = 0;
              let upper = index - 1;
              let found = -1;
              while (lower <= upper) {
                const middle = lower + Math.floor((upper - lower) / 2);
                if (points[middle][0] <= cutoff) { found = middle; lower = middle + 1; }
                else upper = middle - 1;
              }
              if (found >= 0) {
                baselineSeconds = points[found][0];
                baselineCompleted = points[found][2];
              } else if (points[0][0] > 0 && current[0] > windowSeconds) {
                baselineSeconds = points[0][0];
                baselineCompleted = points[0][2];
              }
            }
            const elapsed = current[0] - baselineSeconds;
            return elapsed <= 0 ? current[1] : Math.max(0, current[2] - baselineCompleted) / elapsed;
          }

          function renderThroughput() {
            const card = cards.throughput;
            const svg = card.querySelector('svg');
            const size = dimensions(card, true);
            const points = report.timeline || [];
            const seconds = Number(document.getElementById('throughput-smoothing').value) || 5;
            const values = points.map((_, index) => smoothedTps(points, index, seconds));
            const maximum = Math.max(1, ...values);
            const usersMaximum = Math.max(1, report.configuredUserCount || 0, ...points.map(point => point[3]));
            const duration = Math.max(report.elapsedSeconds || 0, points.at(-1)?.[0] || 0);
            setSvgSize(svg, size);
            svg.innerHTML = points.length
              ? axisMarkup(size, duration, maximum, value => `${formatNumber(value, value < 10 ? 2 : 0)} TPS`, usersMaximum, value => `${Math.round(value)} 人`)
                + `<path class="series-line throughput-line" d="${pathFor(values, maximum, size)}"></path>`
                + `<path class="series-line user-line" d="${pathFor(points.map(point => point[3]), usersMaximum, size)}"></path>`
              : emptyMarkup(size);
            document.getElementById('throughput-legend').textContent = `${seconds} 秒平滑 TPS`;
            if (!points.length) return;
            bindLineHover(card, size, points.length, index => [
              { y: yAt(values[index], maximum, size), color: '#1677ff' },
              { y: yAt(points[index][3], usersMaximum, size), color: '#7458c4' }
            ], (index, event) => showTooltip(card, event, `${formatClock(points[index][0])} · ${formatElapsed(points[index][0])}`, [
              [`${seconds} 秒平滑 TPS`, formatNumber(values[index], 2), '#1677ff'],
              ['原始 TPS', formatNumber(points[index][1], 2), '#6ca9ff'],
              ['活跃用户', formatNumber(points[index][3], 0), '#7458c4']
            ]));
          }

          function renderLatency() {
            const card = cards.latency;
            const svg = card.querySelector('svg');
            const size = dimensions(card, true);
            const points = report.timeline || [];
            const recent = document.getElementById('latency-mode').value === 'recent';
            const offset = recent ? 7 : 4;
            const average = points.map(point => point[offset]);
            const p95 = points.map(point => point[offset + 1]);
            const p99 = points.map(point => point[offset + 2]);
            const maximum = Math.max(1, ...average, ...p95, ...p99);
            const usersMaximum = Math.max(1, report.configuredUserCount || 0, ...points.map(point => point[3]));
            const duration = Math.max(report.elapsedSeconds || 0, points.at(-1)?.[0] || 0);
            setSvgSize(svg, size);
            svg.innerHTML = points.length
              ? axisMarkup(size, duration, maximum, value => `${formatNumber(value, value < 10 ? 2 : 0)} ms`, usersMaximum, value => `${Math.round(value)} 人`)
                + `<path class="series-line average-line" d="${pathFor(average, maximum, size)}"></path>`
                + `<path class="series-line p95-line" d="${pathFor(p95, maximum, size)}"></path>`
                + `<path class="series-line p99-line" d="${pathFor(p99, maximum, size)}"></path>`
                + `<path class="series-line user-line" d="${pathFor(points.map(point => point[3]), usersMaximum, size)}"></path>`
              : emptyMarkup(size);
            if (!points.length) return;
            bindLineHover(card, size, points.length, index => [
              { y: yAt(average[index], maximum, size), color: '#13a274' },
              { y: yAt(p95[index], maximum, size), color: '#ee9826' },
              { y: yAt(p99[index], maximum, size), color: '#d83b4e' },
              { y: yAt(points[index][3], usersMaximum, size), color: '#7458c4' }
            ], (index, event) => showTooltip(card, event, `${formatClock(points[index][0])} · ${formatElapsed(points[index][0])}`, [
              [recent ? '最近 5 秒平均' : '累计平均', formatMilliseconds(average[index]), '#13a274'],
              ['P95', formatMilliseconds(p95[index]), '#ee9826'],
              ['P99', formatMilliseconds(p99[index]), '#d83b4e'],
              ['活跃用户', formatNumber(points[index][3], 0), '#7458c4']
            ]));
          }

          function adaptivePercentageMaximum(values) {
            const maximum = Math.max(0, ...values.map(value => clamp(finite(value), 0, 100)));
            if (maximum <= 0) return 1;
            const padded = Math.min(100, maximum * 1.12);
            const magnitude = Math.pow(10, Math.floor(Math.log10(padded)));
            const step = magnitude / 2;
            return Math.min(100, Math.ceil(padded / step) * step);
          }

          function initialBuckets() {
            return (report.initialBuckets || []).map(bucket => ({
              start: bucket[0], end: bucket[1], total: bucket[2], failed: bucket[3], slow: bucket[4]
            }));
          }

          function buildBuckets(slowThreshold) {
            if (!report.hasAllResults || !allRows.length) return initialBuckets();
            let duration = Math.max(.001, report.elapsedSeconds || 0);
            allRows.forEach(row => { duration = Math.max(duration, Math.max(0, finite(row.s))); });
            const bucketCount = clamp(Math.ceil(duration / 2), 1, 60);
            const bucketWidth = duration / bucketCount;
            const buckets = Array.from({ length: bucketCount }, (_, index) => ({
              start: index * bucketWidth, end: (index + 1) * bucketWidth, total: 0, failed: 0, slow: 0
            }));
            allRows.forEach(row => {
              const index = clamp(Math.floor(Math.max(0, finite(row.s)) / bucketWidth), 0, bucketCount - 1);
              const bucket = buckets[index];
              bucket.total++;
              if (!row.ok) bucket.failed++;
              if (finite(row.ms) > slowThreshold) bucket.slow++;
            });
            return buckets;
          }

          function renderDistribution(kind) {
            const card = cards[kind];
            const svg = card.querySelector('svg');
            const size = dimensions(card, true);
            const thresholdInput = document.getElementById('slow-threshold');
            const threshold = Math.max(0, finite(thresholdInput.value));
            const buckets = buildBuckets(threshold);
            const isSlow = kind === 'slow';
            const counts = buckets.map(bucket => isSlow ? bucket.slow : bucket.failed);
            const rates = buckets.map((bucket, index) => bucket.total
              ? counts[index] * 100 / bucket.total
              : 0);
            const countMaximum = Math.max(1, ...counts);
            const rateMaximum = adaptivePercentageMaximum(rates);
            const duration = Math.max(report.elapsedSeconds || 0, buckets.at(-1)?.end || 0);
            setSvgSize(svg, size);
            if (!buckets.length) {
              svg.innerHTML = emptyMarkup(size);
              return;
            }
            const slotWidth = size.plotWidth / buckets.length;
            const barWidth = Math.max(2, slotWidth * .62);
            const barClass = isSlow ? 'slow-bar' : 'error-bar';
            const rateClass = isSlow ? 'slow-rate-line' : 'error-rate-line';
            let markup = axisMarkup(size, duration, countMaximum, formatCount, rateMaximum, value => formatPercent(value));
            counts.forEach((value, index) => {
              const height = clamp(value / countMaximum, 0, 1) * size.plotHeight;
              const x = size.left + index * slotWidth + (slotWidth - barWidth) / 2;
              markup += `<rect class="${barClass}" x="${x}" y="${size.plotBottom - height}" width="${barWidth}" height="${height}"></rect>`;
            });
            markup += `<path class="series-line ${rateClass}" d="${bucketPathFor(rates, rateMaximum, size)}"></path>`;
            svg.innerHTML = markup;
            const layer = createHoverLayer(svg);
            const hit = document.createElementNS(svgNamespace, 'rect');
            hit.setAttribute('class', 'chart-hit-area');
            hit.setAttribute('x', String(size.left));
            hit.setAttribute('y', String(size.top));
            hit.setAttribute('width', String(size.plotWidth));
            hit.setAttribute('height', String(size.plotHeight));
            svg.appendChild(hit);
            hit.addEventListener('pointermove', event => {
              const rect = svg.getBoundingClientRect();
              const svgX = (event.clientX - rect.left) * size.width / rect.width;
              const index = clamp(Math.floor((svgX - size.left) / slotWidth), 0, buckets.length - 1);
              const x = size.left + (index + .5) * slotWidth;
              layer.innerHTML = `<line class="hover-line" x1="${x}" y1="${size.top}" x2="${x}" y2="${size.plotBottom}"></line>`
                + `<circle class="hover-point" cx="${x}" cy="${yAt(rates[index], rateMaximum, size)}" r="4" style="fill:${isSlow ? '#d47a00' : '#c23043'}"></circle>`;
              const bucket = buckets[index];
              showTooltip(card, event, `${formatClock(bucket.start)}–${formatClock(bucket.end)}`, [
                [isSlow ? '慢请求数' : '错误请求数', formatCount(counts[index]), isSlow ? '#f1a53b' : '#f06a78'],
                [isSlow ? '慢请求率' : '错误率', formatPercent(rates[index]), isSlow ? '#d47a00' : '#c23043'],
                ['全部请求', formatCount(bucket.total), '#8491a5'],
                ...(isSlow ? [['耗时定义', `> ${formatNumber(threshold, 0)} ms`, '#8491a5']] : [])
              ]);
            });
            hit.addEventListener('pointerleave', () => hideTooltip(card));
          }

          const renderers = {
            throughput: renderThroughput,
            latency: renderLatency,
            errors: () => renderDistribution('errors'),
            slow: () => renderDistribution('slow')
          };

          function render(kind) {
            const card = cards[kind];
            if (!card) return;
            const viewport = card.querySelector('.chart-viewport');
            const previousMaximum = Math.max(1, viewport.scrollWidth - viewport.clientWidth);
            const scrollRatio = viewport.scrollLeft / previousMaximum;
            renderers[kind]();
            requestAnimationFrame(() => {
              const maximum = Math.max(0, viewport.scrollWidth - viewport.clientWidth);
              viewport.scrollLeft = maximum * scrollRatio;
            });
            card.querySelector('[data-zoom-label]').textContent = `${zooms[kind].toFixed(1).replace('.0', '')}x`;
            card.querySelector('[data-zoom-out]').disabled = zooms[kind] <= minimumZoom;
            card.querySelector('[data-zoom-in]').disabled = zooms[kind] >= maximumZoom;
          }

          Object.entries(cards).forEach(([kind, card]) => {
            card.querySelector('[data-zoom-out]').addEventListener('click', () => {
              zooms[kind] = clamp(zooms[kind] - zoomStep, minimumZoom, maximumZoom);
              render(kind);
            });
            card.querySelector('[data-zoom-in]').addEventListener('click', () => {
              zooms[kind] = clamp(zooms[kind] + zoomStep, minimumZoom, maximumZoom);
              render(kind);
            });
            card.querySelector('[data-fullscreen]').addEventListener('click', async () => {
              if (document.fullscreenElement === card) await document.exitFullscreen();
              else await card.requestFullscreen();
            });
          });

          document.getElementById('throughput-smoothing').addEventListener('change', renderThroughput);
          document.getElementById('latency-mode').addEventListener('change', renderLatency);
          document.getElementById('slow-threshold').addEventListener('change', event => {
            const value = Math.max(0, finite(event.currentTarget.value));
            event.currentTarget.value = String(Math.round(value));
            renderDistribution('slow');
          });
          document.getElementById('slow-threshold').addEventListener('keydown', event => {
            if (event.key === 'Enter') { event.currentTarget.blur(); }
          });
          document.addEventListener('fullscreenchange', () => setTimeout(() => Object.keys(cards).forEach(render), 0));
          let resizeTimer;
          window.addEventListener('resize', () => {
            clearTimeout(resizeTimer);
            resizeTimer = setTimeout(() => Object.keys(cards).forEach(render), 120);
          });
          Object.keys(cards).forEach(render);
        })();
        </script>
        """;

    private const string DetailScript = """
        <script>
        (() => {
          'use strict';
          const $ = id => document.getElementById(id);
          const allRows = window.__pressureReportRows || JSON.parse($('detail-data').textContent);
          const pageSize = 100;
          const hasPayload = allRows.some(row => Object.prototype.hasOwnProperty.call(row, 'q'));
          let filteredRows = allRows;
          let currentPage = 1;

          const readNumber = id => {
            const value = $(id)?.value?.trim();
            if (!value) return null;
            const parsed = Number(value);
            return Number.isFinite(parsed) ? parsed : null;
          };
          const includes = (source, value) => !value || (source || '').toLocaleLowerCase().includes(value);
          const matchesTime = (time, from, to) => {
            if (!from && !to) return true;
            if (from && to && from > to) return time >= from || time <= to;
            return (!from || time >= from) && (!to || time <= to);
          };
          const addCell = (row, text, className) => {
            const cell = document.createElement('td');
            if (className) cell.className = className;
            cell.textContent = text;
            row.appendChild(cell);
            return cell;
          };

          function applyFilters() {
            const from = $('filter-time-from').value;
            const to = $('filter-time-to').value;
            const userMin = readNumber('filter-user-min');
            const userMax = readNumber('filter-user-max');
            const iterationMin = readNumber('filter-iteration-min');
            const iterationMax = readNumber('filter-iteration-max');
            const msMin = readNumber('filter-ms-min');
            const msMax = readNumber('filter-ms-max');
            const status = $('filter-status').value;
            const error = $('filter-error').value.trim().toLocaleLowerCase();
            const request = $('filter-request')?.value.trim().toLocaleLowerCase() || '';
            const response = $('filter-response')?.value.trim().toLocaleLowerCase() || '';
            filteredRows = allRows.filter(row =>
              matchesTime(row.t, from, to)
              && (userMin === null || row.u >= userMin)
              && (userMax === null || row.u <= userMax)
              && (iterationMin === null || row.i >= iterationMin)
              && (iterationMax === null || row.i <= iterationMax)
              && (msMin === null || row.ms >= msMin)
              && (msMax === null || row.ms <= msMax)
              && (status === 'all' || status === 'success' && row.ok || status === 'failure' && !row.ok)
              && includes(row.e, error)
              && includes(row.q, request)
              && includes(row.r, response));
            currentPage = 1;
            render();
          }

          function render() {
            const pageCount = Math.max(1, Math.ceil(filteredRows.length / pageSize));
            currentPage = Math.min(Math.max(1, currentPage), pageCount);
            const body = $('detail-body');
            body.replaceChildren();
            const start = (currentPage - 1) * pageSize;
            const pageRows = filteredRows.slice(start, start + pageSize);
            if (pageRows.length === 0) {
              const row = document.createElement('tr');
              const cell = addCell(row, '没有符合筛选条件的请求', 'table-empty');
              cell.colSpan = hasPayload ? 8 : 7;
              body.appendChild(row);
            } else {
              pageRows.forEach((item, index) => {
                const row = document.createElement('tr');
                addCell(row, String(start + index + 1));
                addCell(row, String(item.u));
                addCell(row, String(item.i));
                addCell(row, item.t);
                addCell(row, `${item.ms.toFixed(2)} ms`);
                const statusCell = document.createElement('td');
                const badge = document.createElement('span');
                badge.className = item.ok ? 'success' : 'failure';
                badge.textContent = item.ok ? '成功' : '失败';
                statusCell.appendChild(badge);
                row.appendChild(statusCell);
                addCell(row, item.e || '—');
                if (hasPayload) {
                  const cell = document.createElement('td');
                  cell.className = 'payload-cell';
                  const details = document.createElement('details');
                  const summary = document.createElement('summary');
                  summary.textContent = '查看';
                  details.appendChild(summary);
                  [['请求内容', item.q], ['响应内容', item.r]].forEach(([title, value]) => {
                    const heading = document.createElement('h4');
                    heading.textContent = title;
                    const pre = document.createElement('pre');
                    pre.textContent = value || '';
                    details.append(heading, pre);
                  });
                  cell.appendChild(details);
                  row.appendChild(cell);
                }
                body.appendChild(row);
              });
            }

            $('detail-count').textContent = `筛选后 ${filteredRows.length.toLocaleString()} 条 / 全部 ${allRows.length.toLocaleString()} 条`;
            $('page-label').textContent = `第 ${currentPage} / ${pageCount} 页`;
            $('page-target').max = String(pageCount);
            $('page-target').value = String(currentPage);
            $('page-first').disabled = currentPage <= 1;
            $('page-prev').disabled = currentPage <= 1;
            $('page-next').disabled = currentPage >= pageCount;
            $('page-last').disabled = currentPage >= pageCount;
          }

          $('filter-apply').addEventListener('click', applyFilters);
          $('filter-clear').addEventListener('click', () => {
            document.querySelectorAll('.detail-filters input').forEach(input => input.value = '');
            $('filter-status').value = 'all';
            filteredRows = allRows;
            currentPage = 1;
            render();
          });
          $('page-first').addEventListener('click', () => { currentPage = 1; render(); });
          $('page-prev').addEventListener('click', () => { currentPage--; render(); });
          $('page-next').addEventListener('click', () => { currentPage++; render(); });
          $('page-last').addEventListener('click', () => { currentPage = Math.ceil(filteredRows.length / pageSize); render(); });
          $('page-go').addEventListener('click', () => { currentPage = Number($('page-target').value) || 1; render(); });
          $('page-target').addEventListener('keydown', event => {
            if (event.key === 'Enter') { currentPage = Number(event.currentTarget.value) || 1; render(); }
          });
          render();
        })();
        </script>
        """;

    private const string Styles = """
        :root{font-family:Inter,-apple-system,BlinkMacSystemFont,"Segoe UI","PingFang SC","Microsoft YaHei",sans-serif;color:#172033;background:#f3f6fb}*{box-sizing:border-box}body{margin:0}button,input,select{font:inherit}main{max-width:1600px;margin:auto;padding:24px}header{display:flex;align-items:center;gap:14px;margin-bottom:18px}.logo{width:46px;height:46px;display:grid;place-items:center;border-radius:13px;color:#fff;background:linear-gradient(145deg,#2488ff,#075ee5);font-size:25px;font-weight:800;font-style:italic}h1{margin:0;font-size:23px}header p,.chart p,.section-title p{margin:4px 0 0;color:#8993a1;font-size:12px}.status{margin-left:auto;padding:7px 13px;border-radius:999px;font-size:12px;font-weight:700}.status.ok{color:#087f58;background:#dcf8ed}.status.warn{color:#b92b3d;background:#ffe7ea}.card{overflow:hidden;border:1px solid #e0e6ef;border-radius:11px;background:#fff;box-shadow:0 3px 12px rgba(26,47,76,.04)}.card h2{margin:0;font-size:16px}.config{padding:18px}.config-grid{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:14px;margin:15px 0}.config-grid div{min-width:0;padding:10px;border-radius:8px;background:#f7f9fc}.config-grid span,.metrics span{display:block;color:#8993a1;font-size:11px}.config-grid b{display:block;margin-top:5px;overflow-wrap:anywhere;font-size:13px}.config h3{margin:18px 0 8px;font-size:13px}pre{max-height:300px;overflow:auto;margin:0;padding:13px;border-radius:8px;color:#33435a;background:#f7f9fc;white-space:pre-wrap;overflow-wrap:anywhere}.metrics{display:grid;grid-template-columns:repeat(6,minmax(0,1fr));gap:12px;margin:14px 0}.metrics article{padding:15px;border:1px solid #e0e6ef;border-radius:10px;background:#fff}.metrics b{display:block;margin:8px 0 4px;font-size:20px}.metrics small{color:#8d97a5}.charts{display:grid;grid-template-columns:1fr;gap:14px}.chart{padding:0}.chart-heading{display:flex;align-items:center;justify-content:space-between;padding:16px 18px 8px}.fullscreen-button{width:34px;height:34px;border:1px solid #d8e1ed;border-radius:7px;color:#61738a;background:#fff;cursor:pointer}.chart-toolbar{display:flex;align-items:center;gap:10px;min-height:42px;padding:0 18px 10px;flex-wrap:wrap;color:#718096;font-size:11px}.compact-control select,.threshold-control input{height:30px;border:1px solid #d8e1ed;border-radius:6px;color:#40536b;background:#fff;outline:none}.compact-control select{min-width:150px;padding:0 28px 0 9px}.threshold-control{display:inline-flex;align-items:center;gap:6px}.threshold-control input{width:82px;padding:0 8px}.legend{display:inline-flex!important;align-items:center;gap:5px;color:#718096!important}.legend i,.chart-tooltip i{display:inline-block;width:8px;height:8px;border-radius:50%}.blue-dot{background:#1677ff}.green-dot{background:#13a274}.orange-dot{background:#ee9826}.red-dot{background:#d83b4e}.purple-dot{background:#7458c4}.error-bar-dot{background:#f06a78}.error-rate-dot{background:#c23043}.slow-bar-dot{background:#f1a53b}.slow-rate-dot{background:#d47a00}.zoom-control{display:inline-flex!important;align-items:center;margin-left:2px;border:1px solid #d8e1ed;border-radius:6px;overflow:hidden}.zoom-control button{width:30px;height:28px;border:0;color:#56708e;background:#fff;cursor:pointer}.zoom-control button:disabled{color:#c6ced9;cursor:not-allowed}.zoom-control b{min-width:38px;color:#526176;text-align:center;font-size:11px}.chart-viewport{position:relative;width:100%;height:390px;overflow-x:auto;overflow-y:hidden;border-top:1px solid #eef1f5;background:#fff}.chart svg{display:block;max-width:none;touch-action:none}.axis-label{fill:#7b8da4;font-size:10px}.axis-line,.axis-tick{stroke:#cdd7e4;stroke-width:1}.chart-grid-line{stroke:#e8edf4;stroke-width:1}.series-line{fill:none;stroke-width:2.2;stroke-linejoin:round;stroke-linecap:round}.throughput-line{stroke:#1677ff}.average-line{stroke:#13a274}.p95-line{stroke:#ee9826}.p99-line{stroke:#d83b4e}.user-line{stroke:#7458c4;stroke-dasharray:6 5}.error-bar{fill:#f06a78;opacity:.76}.error-rate-line{stroke:#c23043}.slow-bar{fill:#f1a53b;opacity:.76}.slow-rate-line{stroke:#d47a00}.chart-hit-area{fill:transparent;pointer-events:all}.hover-line{stroke:#8fa1b7;stroke-width:1;stroke-dasharray:4 3;pointer-events:none}.hover-point{stroke:#fff;stroke-width:2;pointer-events:none}.chart-empty{fill:#9aa6b5;font-size:13px}.chart-tooltip{position:absolute;z-index:4;min-width:175px;padding:10px 12px;border:1px solid #d8e1ed;border-radius:8px;color:#40516a;background:rgba(255,255,255,.96);box-shadow:0 8px 24px rgba(25,45,75,.14);pointer-events:none;font-size:11px}.chart-tooltip b{display:block;margin-bottom:7px;font-size:12px}.chart-tooltip div{display:flex;align-items:center;justify-content:space-between;gap:18px;margin-top:5px}.chart-tooltip div span{display:inline-flex;align-items:center;gap:5px;color:#718096}.chart-tooltip strong{white-space:nowrap;color:#33445b}.chart:fullscreen{padding:0;background:#fff}.chart:fullscreen .chart-viewport{height:calc(100vh - 125px)}.details{margin-top:14px}.section-title{display:flex;align-items:center;justify-content:space-between;padding:15px 18px;border-bottom:1px solid #eef1f5}.section-title b{font-size:12px;color:#526176}.empty{min-height:120px;display:grid;place-items:center;color:#8fa297}.detail-filters{display:grid;grid-template-columns:repeat(5,minmax(150px,1fr));gap:10px;padding:14px 18px;border-bottom:1px solid #eef1f5;background:#fbfcfe}.detail-filters label{display:grid;gap:5px;color:#637187;font-size:10px}.detail-filters input,.detail-filters select{width:100%;height:34px;padding:0 9px;border:1px solid #d8e1ed;border-radius:6px;color:#34465e;outline:none;background:#fff}.detail-filters input:focus,.detail-filters select:focus{border-color:#78aef2;box-shadow:0 0 0 2px rgba(22,119,255,.1)}.detail-filter-actions{display:flex;align-items:end;justify-content:flex-end;gap:8px}.detail-filter-actions button,.detail-pagination button{height:32px;padding:0 12px;border:1px solid #d8e1ed;border-radius:6px;color:#52657d;background:#fff;cursor:pointer}.detail-filter-actions .primary{border-color:#1677ff;color:#fff;background:#1677ff}.detail-result-meta{display:flex;justify-content:space-between;padding:10px 18px;color:#718096;background:#fff;font-size:11px}.table-wrap{max-height:680px;overflow:auto}table{width:100%;border-collapse:collapse;font-size:11px}th,td{padding:9px 11px;border-bottom:1px solid #eef1f5;text-align:left;vertical-align:top}th{position:sticky;z-index:1;top:0;color:#66758a;background:#f8fafc}.success,.failure{display:inline-block;padding:2px 7px;border-radius:999px;font-weight:700}.success{color:#087f58;background:#dcf8ed}.failure{color:#b92b3d;background:#ffe7ea}.payload-cell{min-width:80px}.payload-cell summary{color:#0958d9;cursor:pointer}.payload-cell h4{margin:10px 0 5px}.payload-cell pre{min-width:320px;max-width:680px}.table-empty{height:90px;color:#8fa297;text-align:center;vertical-align:middle}.detail-pagination{display:flex;align-items:center;justify-content:flex-end;gap:6px;padding:10px 18px;border-top:1px solid #eef1f5;background:#fafbfd}.detail-pagination b{min-width:110px;text-align:center;color:#526176;font-size:11px}.detail-pagination button:disabled{color:#b5bdc8;background:#f4f6f9;cursor:not-allowed}.detail-pagination span{display:inline-flex;align-items:center;gap:6px;margin-left:8px;color:#718096;font-size:10px}.detail-pagination input{width:68px;height:32px;padding:0 7px;border:1px solid #d8e1ed;border-radius:6px}footer{padding:22px;text-align:center;color:#949ca8;font-size:11px}@media(max-width:1000px){.config-grid{grid-template-columns:repeat(2,1fr)}.metrics{grid-template-columns:repeat(3,1fr)}.detail-filters{grid-template-columns:repeat(2,minmax(150px,1fr))}}@media(max-width:640px){main{padding:10px}.metrics{grid-template-columns:repeat(2,1fr)}.detail-filters{grid-template-columns:1fr}.detail-pagination{align-items:flex-start;flex-wrap:wrap}.section-title{align-items:flex-start;flex-direction:column;gap:8px}}@media print{body{background:#fff}main{max-width:none;padding:0}.card,.metrics article{box-shadow:none}.chart{break-inside:avoid;page-break-inside:avoid}.chart-toolbar,.fullscreen-button{display:none}.chart-viewport{height:300px;overflow:hidden}.chart svg{max-width:100%;height:300px}.detail-filters,.detail-pagination{display:none}.table-wrap{max-height:none;overflow:visible}th{position:static}}
        """;
}
