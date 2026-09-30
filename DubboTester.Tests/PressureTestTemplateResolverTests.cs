using System.Text.Json;
using DubboTester.Services;
using Xunit;

namespace DubboTester.Tests;

public sealed class PressureTestTemplateResolverTests
{
    [Fact]
    public void Resolve_ReplacesIndexesAndGeneratesValidRandomValues()
    {
        const string template = """
            {
              "user": {{user}},
              "iteration": {{iteration}},
              "key": "u{{user}}-i{{iteration}}-{{randomString:12}}",
              "random": {{randomInt:10:20}},
              "id": "{{guid}}"
            }
            """;

        string resolved = PressureTestTemplateResolver.Resolve(template, 3, 7);
        using JsonDocument document = JsonDocument.Parse(resolved);
        JsonElement root = document.RootElement;

        Assert.Equal(3, root.GetProperty("user").GetInt32());
        Assert.Equal(7, root.GetProperty("iteration").GetInt32());
        Assert.StartsWith("u3-i7-", root.GetProperty("key").GetString());
        Assert.InRange(root.GetProperty("random").GetInt32(), 10, 20);
        Assert.Equal(32, root.GetProperty("id").GetString()!.Length);
    }

    [Fact]
    public void FormatJsonTemplate_PreservesQuotedAndBareVariables()
    {
        const string template = """{"key":"user-{{user}}","number":{{randomInt:1:9}},"iteration":{{iteration}}}""";

        string formatted = PressureTestTemplateResolver.FormatJsonTemplate(template);

        Assert.Contains("\"key\": \"user-{{user}}\"", formatted);
        Assert.Contains("\"number\": {{randomInt:1:9}}", formatted);
        Assert.Contains("\"iteration\": {{iteration}}", formatted);
        using JsonDocument resolved = JsonDocument.Parse(PressureTestTemplateResolver.Resolve(formatted, 2, 4));
        Assert.Equal("user-2", resolved.RootElement.GetProperty("key").GetString());
        Assert.Equal(4, resolved.RootElement.GetProperty("iteration").GetInt32());
    }

    [Fact]
    public void FormatJsonTemplate_WrapsCommaSeparatedMethodArguments()
    {
        string formatted = PressureTestTemplateResolver.FormatJsonTemplate("{{user}}, true");

        Assert.Equal("[\n  {{user}},\n  true\n]", formatted.Replace("\r", string.Empty));
        Assert.Equal(2, JsonArgumentParser.ParseArguments(
            PressureTestTemplateResolver.Resolve(formatted, 6, 1)).Length);
    }

    [Fact]
    public void Resolve_RejectsUnknownVariable()
    {
        FormatException exception = Assert.Throws<FormatException>(() =>
            PressureTestTemplateResolver.Resolve("{{unknown}}", 1, 1));

        Assert.Contains("不支持的压测变量", exception.Message);
    }

    [Fact]
    public void Percentile_UsesNearestRank()
    {
        double[] sorted = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

        Assert.Equal(5, DubboPressureTestRunner.Percentile(sorted, .50));
        Assert.Equal(10, DubboPressureTestRunner.Percentile(sorted, .95));
        Assert.Equal(10, DubboPressureTestRunner.Percentile(sorted, .99));
    }
}
