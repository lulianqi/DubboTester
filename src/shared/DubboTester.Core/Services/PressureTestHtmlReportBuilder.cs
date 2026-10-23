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

        html.Append("<section class=\"charts\">")
            .Append(ThroughputChart(snapshot.Timeline, configuration.UserCount, throughputSmoothingSeconds))
            .Append(LatencyChart(snapshot.Timeline, configuration.UserCount, useRecentLatency))
            .Append(ErrorDistributionChart(snapshot))
            .Append(SlowDistributionChart(snapshot, configuration.SlowRequestThresholdMilliseconds))
            .Append("</section>");

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

    private static string ThroughputChart(
        IReadOnlyList<PressureTestPoint> points,
        int configuredUserCount,
        int smoothingSeconds)
    {
        double[] smoothedTps = points
            .Select((_, index) => PressureTestChartMetrics.CalculateSmoothedTps(points, index, smoothingSeconds))
            .ToArray();
        double maximum = Math.Max(1, smoothedTps.DefaultIfEmpty(0).Max());
        double userMaximum = Math.Max(1, configuredUserCount);
        return Chart("吞吐趋势", $"500 ms 采样 · {smoothingSeconds} 秒滑动平均 TPS / 活跃用户", [
            (BuildPolyline(points, (_, index) => smoothedTps[index], maximum), "blue"),
            (BuildPolyline(points, point => point.ActiveUsers, userMaximum), "purple")
        ]);
    }

    private static string LatencyChart(
        IReadOnlyList<PressureTestPoint> points,
        int configuredUserCount,
        bool useRecentLatency)
    {
        double Average(PressureTestPoint point) => useRecentLatency
            ? point.RecentAverageMilliseconds
            : point.AverageMilliseconds;
        double P95(PressureTestPoint point) => useRecentLatency
            ? point.RecentP95Milliseconds
            : point.P95Milliseconds;
        double P99(PressureTestPoint point) => useRecentLatency
            ? point.RecentP99Milliseconds
            : point.P99Milliseconds;
        double maximum = Math.Max(1, points.Count == 0
            ? 0
            : points.Max(point => Math.Max(Average(point), Math.Max(P95(point), P99(point)))));
        double userMaximum = Math.Max(1, configuredUserCount);
        string mode = useRecentLatency ? "即时（最近 5 秒）" : "累计";
        return Chart("响应时间趋势", $"{mode}平均 / P95 / P99（ms）/ 活跃用户", [
            (BuildPolyline(points, Average, maximum), "green"),
            (BuildPolyline(points, P95, maximum), "orange"),
            (BuildPolyline(points, P99, maximum), "red"),
            (BuildPolyline(points, point => point.ActiveUsers, userMaximum), "purple")
        ]);
    }

    private static string ErrorDistributionChart(PressureTestSnapshot snapshot)
    {
        IReadOnlyList<PressureTestDistributionBucket> buckets =
            PressureTestDistributionBuilder.Build(
                snapshot.RequestResults,
                snapshot.ElapsedSeconds,
                double.MaxValue);
        return DistributionChart(
            "错误请求分布",
            "柱状图：错误请求数 · 折线：错误率",
            buckets,
            bucket => bucket.FailedRequests,
            bucket => bucket.ErrorRate,
            "error-bar",
            "error-rate");
    }

    private static string SlowDistributionChart(
        PressureTestSnapshot snapshot,
        double slowRequestThresholdMilliseconds)
    {
        IReadOnlyList<PressureTestDistributionBucket> buckets =
            PressureTestDistributionBuilder.Build(
                snapshot.RequestResults,
                snapshot.ElapsedSeconds,
                slowRequestThresholdMilliseconds);
        return DistributionChart(
            "慢请求分布",
            $"耗时 > {slowRequestThresholdMilliseconds:N0} ms · 柱状图：慢请求数 · 折线：慢请求率",
            buckets,
            bucket => bucket.SlowRequests,
            bucket => bucket.SlowRate,
            "slow-bar",
            "slow-rate");
    }

    private static string DistributionChart(
        string title,
        string subtitle,
        IReadOnlyList<PressureTestDistributionBucket> buckets,
        Func<PressureTestDistributionBucket, int> countSelector,
        Func<PressureTestDistributionBucket, double> rateSelector,
        string barClass,
        string lineClass)
    {
        var html = new StringBuilder();
        html.Append("<article class=\"card chart distribution-chart\"><h2>")
            .Append(Encode(title))
            .Append("</h2><p>")
            .Append(Encode(subtitle))
            .Append("</p><svg viewBox=\"0 0 720 180\" preserveAspectRatio=\"none\">")
            .Append("<path class=\"grid\" d=\"M0 45H720 M0 90H720 M0 135H720\"/>");
        if (buckets.Count > 0)
        {
            double maximum = Math.Max(1, buckets.Max(countSelector));
            double rateMaximum = PressureTestChartMetrics.CalculateAdaptivePercentageMaximum(
                buckets.Select(rateSelector));
            double slotWidth = 720d / buckets.Count;
            double barWidth = Math.Max(2, slotWidth * .62);
            for (int index = 0; index < buckets.Count; index++)
            {
                double value = countSelector(buckets[index]);
                double height = Math.Clamp(value / maximum, 0, 1) * 160;
                double x = index * slotWidth + (slotWidth - barWidth) / 2;
                double y = 170 - height;
                html.Append("<rect class=\"").Append(barClass)
                    .Append("\" x=\"").Append(x.ToString("F1", CultureInfo.InvariantCulture))
                    .Append("\" y=\"").Append(y.ToString("F1", CultureInfo.InvariantCulture))
                    .Append("\" width=\"").Append(barWidth.ToString("F1", CultureInfo.InvariantCulture))
                    .Append("\" height=\"").Append(height.ToString("F1", CultureInfo.InvariantCulture))
                    .Append("\"/>");
            }

            string ratePoints = string.Join(' ', buckets.Select((bucket, index) =>
            {
                double x = (index + .5) * slotWidth;
                double y = 170 - Math.Clamp(rateSelector(bucket) / rateMaximum, 0, 1) * 160;
                return $"{x.ToString("F1", CultureInfo.InvariantCulture)},{y.ToString("F1", CultureInfo.InvariantCulture)}";
            }));
            html.Append("<polyline class=\"").Append(lineClass).Append("\" points=\"")
                .Append(ratePoints).Append("\"/>");
        }

        return html.Append("</svg></article>").ToString();
    }

    private static string Chart(string title, string subtitle, IReadOnlyList<(string Points, string ClassName)> lines)
    {
        var html = new StringBuilder();
        html.Append("<article class=\"card chart\"><h2>").Append(Encode(title)).Append("</h2><p>")
            .Append(Encode(subtitle)).Append("</p><svg viewBox=\"0 0 720 180\" preserveAspectRatio=\"none\">")
            .Append("<path class=\"grid\" d=\"M0 45H720 M0 90H720 M0 135H720\"/>");
        foreach ((string points, string className) in lines)
        {
            html.Append("<polyline class=\"").Append(className).Append("\" points=\"").Append(points).Append("\"/>");
        }
        return html.Append("</svg></article>").ToString();
    }

    private static string BuildPolyline(
        IReadOnlyList<PressureTestPoint> points,
        Func<PressureTestPoint, double> selector,
        double maximum) => BuildPolyline(points, (point, _) => selector(point), maximum);

    private static string BuildPolyline(
        IReadOnlyList<PressureTestPoint> points,
        Func<PressureTestPoint, int, double> selector,
        double maximum) => string.Join(' ', points.Select((point, index) =>
    {
        double x = points.Count <= 1 ? 360 : index * 720d / (points.Count - 1);
        double y = 170 - Math.Clamp(selector(point, index) / maximum, 0, 1) * 160;
        return $"{x.ToString("F1", CultureInfo.InvariantCulture)},{y.ToString("F1", CultureInfo.InvariantCulture)}";
    }));

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

    private const string DetailScript = """
        <script>
        (() => {
          'use strict';
          const $ = id => document.getElementById(id);
          const allRows = JSON.parse($('detail-data').textContent);
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
        :root{font-family:Inter,-apple-system,BlinkMacSystemFont,"Segoe UI","PingFang SC","Microsoft YaHei",sans-serif;color:#172033;background:#f3f6fb}*{box-sizing:border-box}body{margin:0}button,input,select{font:inherit}main{max-width:1600px;margin:auto;padding:24px}header{display:flex;align-items:center;gap:14px;margin-bottom:18px}.logo{width:46px;height:46px;display:grid;place-items:center;border-radius:13px;color:#fff;background:linear-gradient(145deg,#2488ff,#075ee5);font-size:25px;font-weight:800;font-style:italic}h1{margin:0;font-size:23px}header p,.chart p,.section-title p{margin:4px 0 0;color:#8993a1;font-size:12px}.status{margin-left:auto;padding:7px 13px;border-radius:999px;font-size:12px;font-weight:700}.status.ok{color:#087f58;background:#dcf8ed}.status.warn{color:#b92b3d;background:#ffe7ea}.card{overflow:hidden;border:1px solid #e0e6ef;border-radius:11px;background:#fff;box-shadow:0 3px 12px rgba(26,47,76,.04)}.card h2{margin:0;font-size:16px}.config{padding:18px}.config-grid{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:14px;margin:15px 0}.config-grid div{min-width:0;padding:10px;border-radius:8px;background:#f7f9fc}.config-grid span,.metrics span{display:block;color:#8993a1;font-size:11px}.config-grid b{display:block;margin-top:5px;overflow-wrap:anywhere;font-size:13px}.config h3{margin:18px 0 8px;font-size:13px}pre{max-height:300px;overflow:auto;margin:0;padding:13px;border-radius:8px;color:#33435a;background:#f7f9fc;white-space:pre-wrap;overflow-wrap:anywhere}.metrics{display:grid;grid-template-columns:repeat(6,minmax(0,1fr));gap:12px;margin:14px 0}.metrics article{padding:15px;border:1px solid #e0e6ef;border-radius:10px;background:#fff}.metrics b{display:block;margin:8px 0 4px;font-size:20px}.metrics small{color:#8d97a5}.charts{display:grid;grid-template-columns:1fr;gap:14px}.chart{padding:18px}.chart svg{width:100%;height:380px;margin-top:10px}.grid{fill:none;stroke:#edf1f6;stroke-width:1}.chart polyline{fill:none;stroke-width:2.2;vector-effect:non-scaling-stroke}.blue{stroke:#1677ff}.green{stroke:#13a274}.orange{stroke:#ee9826}.red{stroke:#d83b4e}.purple{stroke:#7458c4;stroke-dasharray:5 4}.error-bar{fill:#f06a78;opacity:.78}.error-rate{stroke:#c23043}.slow-bar{fill:#f1a53b;opacity:.78}.slow-rate{stroke:#d47a00}.details{margin-top:14px}.section-title{display:flex;align-items:center;justify-content:space-between;padding:15px 18px;border-bottom:1px solid #eef1f5}.section-title b{font-size:12px;color:#526176}.empty{min-height:120px;display:grid;place-items:center;color:#8fa297}.detail-filters{display:grid;grid-template-columns:repeat(5,minmax(150px,1fr));gap:10px;padding:14px 18px;border-bottom:1px solid #eef1f5;background:#fbfcfe}.detail-filters label{display:grid;gap:5px;color:#637187;font-size:10px}.detail-filters input,.detail-filters select{width:100%;height:34px;padding:0 9px;border:1px solid #d8e1ed;border-radius:6px;color:#34465e;outline:none;background:#fff}.detail-filters input:focus,.detail-filters select:focus{border-color:#78aef2;box-shadow:0 0 0 2px rgba(22,119,255,.1)}.detail-filter-actions{display:flex;align-items:end;justify-content:flex-end;gap:8px}.detail-filter-actions button,.detail-pagination button{height:32px;padding:0 12px;border:1px solid #d8e1ed;border-radius:6px;color:#52657d;background:#fff;cursor:pointer}.detail-filter-actions .primary{border-color:#1677ff;color:#fff;background:#1677ff}.detail-result-meta{display:flex;justify-content:space-between;padding:10px 18px;color:#718096;background:#fff;font-size:11px}.table-wrap{max-height:680px;overflow:auto}table{width:100%;border-collapse:collapse;font-size:11px}th,td{padding:9px 11px;border-bottom:1px solid #eef1f5;text-align:left;vertical-align:top}th{position:sticky;z-index:1;top:0;color:#66758a;background:#f8fafc}.success,.failure{display:inline-block;padding:2px 7px;border-radius:999px;font-weight:700}.success{color:#087f58;background:#dcf8ed}.failure{color:#b92b3d;background:#ffe7ea}.payload-cell{min-width:80px}.payload-cell summary{color:#0958d9;cursor:pointer}.payload-cell h4{margin:10px 0 5px}.payload-cell pre{min-width:320px;max-width:680px}.table-empty{height:90px;color:#8fa297;text-align:center;vertical-align:middle}.detail-pagination{display:flex;align-items:center;justify-content:flex-end;gap:6px;padding:10px 18px;border-top:1px solid #eef1f5;background:#fafbfd}.detail-pagination b{min-width:110px;text-align:center;color:#526176;font-size:11px}.detail-pagination button:disabled{color:#b5bdc8;background:#f4f6f9;cursor:not-allowed}.detail-pagination span{display:inline-flex;align-items:center;gap:6px;margin-left:8px;color:#718096;font-size:10px}.detail-pagination input{width:68px;height:32px;padding:0 7px;border:1px solid #d8e1ed;border-radius:6px}footer{padding:22px;text-align:center;color:#949ca8;font-size:11px}@media(max-width:1000px){.config-grid{grid-template-columns:repeat(2,1fr)}.metrics{grid-template-columns:repeat(3,1fr)}.detail-filters{grid-template-columns:repeat(2,minmax(150px,1fr))}}@media(max-width:640px){main{padding:10px}.metrics{grid-template-columns:repeat(2,1fr)}.detail-filters{grid-template-columns:1fr}.detail-pagination{align-items:flex-start;flex-wrap:wrap}.section-title{align-items:flex-start;flex-direction:column;gap:8px}}@media print{body{background:#fff}main{max-width:none;padding:0}.card,.metrics article{box-shadow:none}.chart{break-inside:avoid;page-break-inside:avoid}.chart svg{height:300px}.detail-filters,.detail-pagination{display:none}.table-wrap{max-height:none;overflow:visible}th{position:static}}
        """;
}
