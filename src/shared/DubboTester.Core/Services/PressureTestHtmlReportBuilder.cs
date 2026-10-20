using System.Globalization;
using System.Net;
using System.Text;
using DubboTester.Models;

namespace DubboTester.Services;

public static class PressureTestHtmlReportBuilder
{
    private const int MaximumDetailRows = 20_000;

    public static string Build(
        PressureTestConfiguration configuration,
        PressureTestSnapshot snapshot,
        DateTimeOffset startedAt,
        DateTimeOffset finishedAt,
        bool includeAllRequests)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(snapshot);

        PressureTestRequestResult[] filtered = snapshot.RequestResults
            .Where(item => includeAllRequests || !item.Success)
            .ToArray();
        PressureTestRequestResult[] displayed = filtered.Take(MaximumDetailRows).ToArray();
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
            .Append(Item("连接地址", configuration.Invocation.Route == InvocationRoute.Registry
                ? configuration.Settings.ZooKeeperAddress
                : $"{configuration.Invocation.Host}:{configuration.Invocation.Port}"))
            .Append(Item("服务", configuration.Invocation.ServiceName))
            .Append(Item("方法", configuration.Invocation.MethodName))
            .Append(Item("Java 参数类型", configuration.Invocation.JavaParameterTypes))
            .Append(Item("版本 / 分组", $"{EmptyAsDash(configuration.Invocation.Version)} / {EmptyAsDash(configuration.Invocation.Group)}"))
            .Append(Item("并发用户", configuration.UserCount.ToString("N0", CultureInfo.InvariantCulture)))
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
            .Append(Item("记录请求与响应", configuration.RecordRequestResponse ? "开启" : "关闭"))
            .Append(Item("业务断言", PressureTestBusinessAssertionEvaluator.Describe(configuration.BusinessAssertion)))
            .Append(Item("开始时间", startedAt.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)))
            .Append(Item("结束时间", finishedAt.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)))
            .Append("</div><h3>请求参数模板</h3><pre>")
            .Append(Encode(configuration.Invocation.RequestJson))
            .Append("</pre></section>");

        html.Append("<section class=\"metrics\">")
            .Append(Metric("平均响应", $"{snapshot.AverageMilliseconds:F2} ms", "所有已完成请求"))
            .Append(Metric("TPS", snapshot.Tps.ToString("F2", CultureInfo.InvariantCulture), "完成数 / 实际墙钟时间"))
            .Append(Metric("成功率", $"{snapshot.SuccessRate:F2}%", $"{snapshot.SuccessCount:N0} 成功 / {snapshot.FailureCount:N0} 失败"))
            .Append(Metric("P95", $"{snapshot.P95Milliseconds:F2} ms", "最终精确分位数"))
            .Append(Metric("P99", $"{snapshot.P99Milliseconds:F2} ms", "最终精确分位数"))
            .Append(Metric("运行时间", $"{snapshot.ElapsedSeconds:F2} s", $"{snapshot.CompletedRequests:N0} 次已完成"))
            .Append("</section>");

        html.Append("<section class=\"charts\">")
            .Append(ThroughputChart(snapshot.Timeline))
            .Append(LatencyChart(snapshot.Timeline))
            .Append("</section>");

        html.Append("<section class=\"card details\"><div class=\"section-title\"><div><h2>运行明细</h2><p>")
            .Append(includeAllRequests ? "全部请求" : "仅失败请求")
            .Append(snapshot.RecordsRequestResponse ? "；包含当前保留的请求与响应" : "；未记录请求与响应正文");
        if (snapshot.DiscardedPayloadCount > 0)
        {
            html.Append("；已因约 200 MB 内存上限清理 ")
                .Append(snapshot.DiscardedPayloadCount.ToString("N0", CultureInfo.InvariantCulture))
                .Append(" 条较旧正文（失败请求优先保留）");
        }
        html.Append("</p></div><b>Min ")
            .Append(snapshot.MinimumMilliseconds.ToString("F2", CultureInfo.InvariantCulture))
            .Append(" ms · Max ")
            .Append(snapshot.MaximumMilliseconds.ToString("F2", CultureInfo.InvariantCulture))
            .Append(" ms</b></div>");

        if (displayed.Length == 0)
        {
            html.Append("<div class=\"empty\">")
                .Append(includeAllRequests ? "暂无请求结果" : "暂无失败请求")
                .Append("</div>");
        }
        else
        {
            html.Append("<table><thead><tr><th>#</th><th>用户</th><th>发送序号</th><th>完成时刻</th><th>耗时</th><th>状态</th><th>错误</th>");
            if (snapshot.RecordsRequestResponse)
            {
                html.Append("<th>请求与响应</th>");
            }
            html.Append("</tr></thead><tbody>");
            for (int index = 0; index < displayed.Length; index++)
            {
                PressureTestRequestResult item = displayed[index];
                html.Append("<tr><td>").Append(index + 1)
                    .Append("</td><td>").Append(item.UserIndex)
                    .Append("</td><td>").Append(item.IterationIndex)
                    .Append("</td><td>").Append(item.CompletedAtSeconds.ToString("F3", CultureInfo.InvariantCulture)).Append(" s")
                    .Append("</td><td>").Append(item.ElapsedMilliseconds.ToString("F2", CultureInfo.InvariantCulture)).Append(" ms")
                    .Append("</td><td><span class=\"").Append(item.Success ? "success" : "failure").Append("\">")
                    .Append(item.Success ? "成功" : "失败")
                    .Append("</span></td><td>").Append(Encode(item.Error ?? "—")).Append("</td>");
                if (snapshot.RecordsRequestResponse)
                {
                    html.Append("<td class=\"payload-cell\"><details><summary>查看</summary><h4>请求内容</h4><pre>")
                        .Append(Encode(RequestText(item)))
                        .Append("</pre><h4>响应内容</h4><pre>")
                        .Append(Encode(ResponseText(item)))
                        .Append("</pre></details></td>");
                }
                html.Append("</tr>");
            }
            html.Append("</tbody></table>");
        }

        if (filtered.Length > displayed.Length)
        {
            html.Append("<p class=\"truncated\">明细共 ").Append(filtered.Length.ToString("N0"))
                .Append(" 条，为避免浏览器生成超大文件，本报告仅展示前 ")
                .Append(MaximumDetailRows.ToString("N0")).Append(" 条。</p>");
        }

        html.Append("</section><footer>由 Dubbo Tester 生成 · 报告仅包含本次压测的内存统计结果</footer></main></body></html>");
        return html.ToString();
    }

    private static string ThroughputChart(IReadOnlyList<PressureTestPoint> points)
    {
        double maximum = Math.Max(1, points.Count == 0 ? 0 : points.Max(point => point.InstantTps));
        return Chart("吞吐趋势", "即时 TPS", [
            (BuildPolyline(points, point => point.InstantTps, maximum), "blue")
        ]);
    }

    private static string LatencyChart(IReadOnlyList<PressureTestPoint> points)
    {
        double maximum = Math.Max(1, points.Count == 0
            ? 0
            : points.Max(point => Math.Max(point.AverageMilliseconds, Math.Max(point.P95Milliseconds, point.P99Milliseconds))));
        return Chart("响应时间趋势", "平均 / P95 / P99（ms）", [
            (BuildPolyline(points, point => point.AverageMilliseconds, maximum), "green"),
            (BuildPolyline(points, point => point.P95Milliseconds, maximum), "orange"),
            (BuildPolyline(points, point => point.P99Milliseconds, maximum), "red")
        ]);
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
        double maximum) => string.Join(' ', points.Select((point, index) =>
    {
        double x = points.Count <= 1 ? 360 : index * 720d / (points.Count - 1);
        double y = 170 - Math.Clamp(selector(point) / maximum, 0, 1) * 160;
        return $"{x.ToString("F1", CultureInfo.InvariantCulture)},{y.ToString("F1", CultureInfo.InvariantCulture)}";
    }));

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

    private const string Styles = """
        :root{font-family:Inter,-apple-system,BlinkMacSystemFont,"Segoe UI","PingFang SC","Microsoft YaHei",sans-serif;color:#172033;background:#f3f6fb}*{box-sizing:border-box}body{margin:0}main{max-width:1440px;margin:auto;padding:24px}header{display:flex;align-items:center;gap:14px;margin-bottom:18px}.logo{width:46px;height:46px;display:grid;place-items:center;border-radius:13px;color:#fff;background:linear-gradient(145deg,#2488ff,#075ee5);font-size:25px;font-weight:800;font-style:italic}h1{margin:0;font-size:23px}header p,.chart p,.section-title p{margin:4px 0 0;color:#8993a1;font-size:12px}.status{margin-left:auto;padding:7px 13px;border-radius:999px;font-size:12px;font-weight:700}.status.ok{color:#087f58;background:#dcf8ed}.status.warn{color:#b92b3d;background:#ffe7ea}.card{overflow:hidden;border:1px solid #e0e6ef;border-radius:11px;background:#fff;box-shadow:0 3px 12px rgba(26,47,76,.04)}.card h2{margin:0;font-size:16px}.config{padding:18px}.config-grid{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:14px;margin:15px 0}.config-grid div{min-width:0;padding:10px;border-radius:8px;background:#f7f9fc}.config-grid span,.metrics span{display:block;color:#8993a1;font-size:11px}.config-grid b{display:block;margin-top:5px;overflow-wrap:anywhere;font-size:13px}.config h3{margin:18px 0 8px;font-size:13px}pre{max-height:300px;overflow:auto;margin:0;padding:13px;border-radius:8px;color:#33435a;background:#f7f9fc;white-space:pre-wrap;overflow-wrap:anywhere}.metrics{display:grid;grid-template-columns:repeat(6,minmax(0,1fr));gap:12px;margin:14px 0}.metrics article{padding:15px;border:1px solid #e0e6ef;border-radius:10px;background:#fff}.metrics b{display:block;margin:8px 0 4px;font-size:20px}.metrics small{color:#8d97a5}.charts{display:grid;grid-template-columns:1fr 1fr;gap:14px}.chart{padding:16px}.chart svg{width:100%;height:260px;margin-top:8px}.grid{fill:none;stroke:#edf1f6;stroke-width:1}.chart polyline{fill:none;stroke-width:2.2;vector-effect:non-scaling-stroke}.blue{stroke:#1677ff}.green{stroke:#13a274}.orange{stroke:#ee9826}.red{stroke:#d83b4e}.details{margin-top:14px}.section-title{display:flex;align-items:center;justify-content:space-between;padding:15px 18px;border-bottom:1px solid #eef1f5}.section-title b{font-size:12px;color:#526176}.empty{min-height:120px;display:grid;place-items:center;color:#8fa297}table{width:100%;border-collapse:collapse;font-size:11px}th,td{padding:9px 11px;border-bottom:1px solid #eef1f5;text-align:left;vertical-align:top}th{position:sticky;top:0;color:#66758a;background:#f8fafc}.success,.failure{display:inline-block;padding:2px 7px;border-radius:999px;font-weight:700}.success{color:#087f58;background:#dcf8ed}.failure{color:#b92b3d;background:#ffe7ea}.payload-cell{min-width:80px}.payload-cell summary{color:#0958d9;cursor:pointer}.payload-cell h4{margin:10px 0 5px}.payload-cell pre{min-width:320px;max-width:680px}.truncated{padding:12px 18px;color:#9a6a2b;background:#fff8e9;font-size:11px}footer{padding:22px;text-align:center;color:#949ca8;font-size:11px}@media(max-width:900px){.config-grid{grid-template-columns:repeat(2,1fr)}.metrics{grid-template-columns:repeat(3,1fr)}.charts{grid-template-columns:1fr}}@media print{body{background:#fff}main{max-width:none;padding:0}.card,.metrics article{box-shadow:none}.chart svg{height:210px}}
        """;
}
