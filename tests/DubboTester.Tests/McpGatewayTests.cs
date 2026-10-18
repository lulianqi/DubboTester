using System.Text.Json;
using DubboTester.Models;
using DubboTester.Services;
using Xunit;

namespace DubboTester.Tests;

public sealed class McpGatewayTests
{
    [Fact]
    public void BuildInputSchema_UsesExactOrderedArgumentCountAndJavaShapes()
    {
        var parameters = new MethodParameterStructure[]
        {
            new()
            {
                Index = 0,
                Structure = new JavaTypeStructure
                {
                    Name = "arg1",
                    JavaType = "java.lang.Long",
                    Kind = JavaTypeKind.Scalar
                }
            },
            new()
            {
                Index = 1,
                Structure = new JavaTypeStructure
                {
                    Name = "arg2",
                    JavaType = "java.util.List<java.lang.String>",
                    Kind = JavaTypeKind.Collection,
                    Children =
                    [
                        new JavaTypeStructure
                        {
                            Name = "元素",
                            JavaType = "java.lang.String",
                            Kind = JavaTypeKind.Scalar
                        }
                    ]
                }
            }
        };

        string json = McpToolSchemaBuilder.BuildInputSchema(parameters, "用户 ID 和标签数组");
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement arguments = document.RootElement.GetProperty("properties").GetProperty("arguments");

        Assert.Equal(2, arguments.GetProperty("minItems").GetInt32());
        Assert.Equal(2, arguments.GetProperty("maxItems").GetInt32());
        JsonElement.ArrayEnumerator prefixItems = arguments.GetProperty("prefixItems").EnumerateArray();
        Assert.Equal("integer", prefixItems.First().GetProperty("type").GetString());
        Assert.Equal("array", prefixItems.Last().GetProperty("type").GetString());
    }

    [Fact]
    public void BuildToolName_IsDeterministicAndProtocolSafe()
    {
        string first = McpToolSchemaBuilder.BuildToolName(
            "com.example.order.OrderService",
            "calculate-total",
            ["java.util.List<com.example.OrderItem>"]);
        string second = McpToolSchemaBuilder.BuildToolName(
            "com.example.order.OrderService",
            "calculate-total",
            ["java.util.List<com.example.OrderItem>"]);

        Assert.Equal(first, second);
        Assert.InRange(first.Length, 1, 64);
        Assert.All(first, character =>
            Assert.True(char.IsAsciiLetterOrDigit(character) || character is '_' or '-'));
    }

    [Fact]
    public void EnsureUniqueToolName_AddsIncrementingSuffixWithinProtocolLimit()
    {
        string preferred = new('a', 64);

        string second = McpToolSchemaBuilder.EnsureUniqueToolName(preferred, [preferred]);
        string third = McpToolSchemaBuilder.EnsureUniqueToolName(preferred, [preferred, second]);

        Assert.EndsWith("_2", second);
        Assert.EndsWith("_3", third);
        Assert.Equal(64, second.Length);
        Assert.Equal(64, third.Length);
    }

    [Fact]
    public void BuildRequestJson_RequiresExactArgumentsArray()
    {
        using JsonDocument document = JsonDocument.Parse("""
            { "arguments": [12, "ACTIVE"] }
            """);
        var arguments = document.RootElement.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.Clone());

        string json = McpGatewayService.BuildRequestJson(arguments, 2);

        Assert.Equal("[12, \"ACTIVE\"]", json);
        Assert.Throws<ArgumentException>(() => McpGatewayService.BuildRequestJson(arguments, 1));
    }

    [Fact]
    public void BuildDebugInvocation_BindsRequestToConfiguredDubboTarget()
    {
        var method = new McpDubboMethodConfiguration
        {
            RegistryRootPath = "/dubbo-2.7.23",
            ServiceName = "com.example.CalculatorService",
            MethodName = "add",
            Version = "1.0.0",
            Group = "test"
        };

        InvocationInput input = McpGatewayService.BuildDebugInvocation(
            method,
            "[12, 30]",
            "int, int");

        Assert.Equal(InvocationRoute.Registry, input.Route);
        Assert.Equal("/dubbo-2.7.23", input.RegistryRootPath);
        Assert.Equal("com.example.CalculatorService", input.ServiceName);
        Assert.Equal("add", input.MethodName);
        Assert.Equal("1.0.0", input.Version);
        Assert.Equal("test", input.Group);
        Assert.Equal("[12, 30]", input.RequestJson);
        Assert.Equal("int, int", input.JavaParameterTypes);
    }

    [Fact]
    public void Validate_RejectsEnabledMethodWithoutAgentGuidance()
    {
        var configuration = new McpGatewayConfiguration
        {
            Enabled = true,
            ZooKeeperAddress = "127.0.0.1:2181",
            AccessToken = "token",
            Methods =
            [
                new McpDubboMethodConfiguration
                {
                    Enabled = true,
                    ToolName = "dubbo_tool",
                    ServiceName = "com.example.Service",
                    MethodName = "run",
                    ParameterTypes = ["java.lang.String"]
                }
            ]
        };

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            McpGatewayService.Validate(configuration));
        Assert.Contains("方法描述、触发条件和参数要求", exception.Message);
    }

    [Fact]
    public void Validate_AllowsEnabledGatewayWithoutTokenWhenAuthenticationIsDisabled()
    {
        var configuration = new McpGatewayConfiguration
        {
            Enabled = true,
            AuthenticationEnabled = false,
            ZooKeeperAddress = "127.0.0.1:2181",
            AccessToken = string.Empty
        };

        McpGatewayService.Validate(configuration);
    }

    [Fact]
    public void Validate_RequiresTokenWhenAuthenticationIsEnabled()
    {
        var configuration = new McpGatewayConfiguration
        {
            Enabled = true,
            AuthenticationEnabled = true,
            ZooKeeperAddress = "127.0.0.1:2181",
            AccessToken = string.Empty
        };

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            McpGatewayService.Validate(configuration));
        Assert.Contains("访问令牌", exception.Message);
    }

    [Fact]
    public async Task TestZooKeeperAsync_RejectsBlankAddressBeforeConnecting()
    {
        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            DubboSession.TestZooKeeperAsync(" ", 1_000));

        Assert.Contains("ZooKeeper 地址", exception.Message);
    }
}
