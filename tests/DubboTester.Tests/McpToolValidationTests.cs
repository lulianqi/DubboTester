using System.Text.Json;
using DubboTester.Models;
using DubboTester.Services;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DubboTester.Tests;

public sealed class McpToolValidationTests
{
    [Theory]
    [InlineData(McpToolValidationStatus.Available, true)]
    [InlineData(McpToolValidationStatus.MetadataWarning, true)]
    [InlineData(McpToolValidationStatus.PendingValidation, false)]
    [InlineData(McpToolValidationStatus.ServiceMissing, false)]
    [InlineData(McpToolValidationStatus.MethodMissing, false)]
    [InlineData(McpToolValidationStatus.SignatureMismatch, false)]
    [InlineData(McpToolValidationStatus.ValidationFailed, false)]
    public void Availability_OnlyAllowsPassedOrWarningTools(McpToolValidationStatus status, bool expected)
    {
        McpDubboMethodConfiguration method = CreateMethod();
        method.ValidationStatus = status;

        Assert.Equal(expected, method.IsValidationUsable);
        Assert.Equal(expected, method.IsAvailableForMcp);
        method.Enabled = false;
        Assert.False(method.IsAvailableForMcp);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(null, 1)]
    [InlineData("Metadata center unavailable; Telnet returned no signatures", 0)]
    [InlineData("Metadata center unavailable; Telnet returned no signatures", 1)]
    public void MissingMetadata_WarnsAndRetainsExplicitArguments(string? error, int parameterCount)
    {
        McpDubboMethodConfiguration method = CreateMethod();
        if (parameterCount == 0) method.ParameterTypes = [];

        var result = McpGatewayService.GetSignatureValidationResult(method, [], error, DateTimeOffset.Now);

        Assert.Equal(McpToolValidationStatus.MetadataWarning, result.Status);
        Assert.Contains("已保存的 Java 参数类型", result.Message);
        Assert.Equal(parameterCount, method.ParameterTypes.Length);
        Assert.Equal("1.0.0", method.Version);
        Assert.Equal("blue", method.Group);
    }

    [Theory]
    [InlineData("java.lang.String", "1.0.0", "blue", McpToolValidationStatus.Available)]
    [InlineData("int", "1.0.0", "blue", McpToolValidationStatus.SignatureMismatch)]
    [InlineData("java.lang.String", "2.0.0", "blue", McpToolValidationStatus.SignatureMismatch)]
    [InlineData("java.lang.String", "1.0.0", "red", McpToolValidationStatus.SignatureMismatch)]
    public void AvailableMetadata_StillChecksExactSignature(
        string parameterType, string version, string group, McpToolValidationStatus expected)
    {
        McpDubboMethodConfiguration method = CreateMethod();
        var signature = new MethodSignatureItem
        {
            ServiceName = method.ServiceName,
            MethodName = method.MethodName,
            ParameterTypes = [parameterType],
            Version = version,
            Group = group
        };

        var result = McpGatewayService.GetSignatureValidationResult(method, [signature], null, DateTimeOffset.Now);

        Assert.Equal(expected, result.Status);
    }

    [Theory]
    [InlineData(false, "echo", "1.0.0", "blue", null)]
    [InlineData(true, "echo", "1.0.0", "blue", McpToolValidationStatus.ValidationFailed)]
    [InlineData(false, "other", "1.0.0", "blue", McpToolValidationStatus.MethodMissing)]
    [InlineData(false, "echo", "2.0.0", "blue", McpToolValidationStatus.SignatureMismatch)]
    [InlineData(false, "echo", "1.0.0", "red", McpToolValidationStatus.SignatureMismatch)]
    public void RegistryValidation_DoesNotTreatProviderOrRoutingFailuresAsMetadataWarnings(
        bool disabled, string providerMethod, string version, string group, McpToolValidationStatus? expected)
    {
        McpDubboMethodConfiguration method = CreateMethod();
        var service = new DubboServiceItem { Name = method.ServiceName, RegistryRootPath = "/dubbo" };
        DubboSession.PopulateServiceDetails(service,
        [
            new DubboProviderItem
            {
                Host = "127.0.0.1", Port = 20880, Scheme = "dubbo",
                ServiceName = method.ServiceName, RegistryUrl = "dubbo://127.0.0.1:20880",
                Methods = [providerMethod], Version = version, Group = group, Disabled = disabled
            }
        ]);

        var result = McpGatewayService.GetServiceValidationFailure(method, service, DateTimeOffset.Now);

        Assert.Equal(expected, result?.Status);
        service.Providers.Clear();
        Assert.Equal(McpToolValidationStatus.ValidationFailed,
            McpGatewayService.GetServiceValidationFailure(method, service, DateTimeOffset.Now)!.Status);
    }

    [Fact]
    public async Task WarningTool_IsAdvertisedPersistedAndNotBlockedAtCallEntry()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"dubbo-mcp-warning-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var provider = new TestAppDataDirectoryProvider(directory);
            using var gateway = new McpGatewayService(provider, new DubboCatalogCache(), new TestRequestContextAccessor());
            await gateway.SaveConfigurationAsync(new McpGatewayConfiguration { RegistryAddress = "127.0.0.1:2181" });
            McpGatewayConfiguration configuration = gateway.GetConfiguration();
            configuration.Enabled = true;
            McpDubboMethodConfiguration method = CreateMethod();
            method.ValidationStatus = McpToolValidationStatus.MetadataWarning;
            configuration.Methods.Add(method);
            await gateway.SaveConfigurationAsync(configuration);

            Assert.Equal(method.ToolName, Assert.Single(gateway.GetTools()).Name);
            using var reloaded = new McpGatewayService(provider, new DubboCatalogCache(), new TestRequestContextAccessor());
            Assert.True(Assert.Single(reloaded.GetConfiguration().Methods).IsAvailableForMcp);

            // An invalid argument count stops before RPC/network access. Reaching
            // argument validation proves the warning did not block the call entry.
            CallToolResult result = await gateway.CallToolAsync(new CallToolRequestParams
            {
                Name = method.ToolName,
                Arguments = new Dictionary<string, JsonElement>
                {
                    ["arguments"] = JsonSerializer.SerializeToElement(Array.Empty<object>())
                }
            }, CancellationToken.None);
            string message = Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
            Assert.Contains("arguments 数量", message);
            Assert.DoesNotContain("当前不可用", message);
            Assert.Equal(1, gateway.GetHistoryPage(1).TotalCount);

            configuration = gateway.GetConfiguration();
            configuration.Methods[0].Enabled = false;
            await gateway.SaveConfigurationAsync(configuration);
            Assert.Empty(gateway.GetTools());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static McpDubboMethodConfiguration CreateMethod() => new()
    {
        Enabled = true, ToolName = "dubbo_echo", RegistryRootPath = "/dubbo",
        ServiceName = "com.example.EchoService", MethodName = "echo",
        ParameterTypes = ["java.lang.String"], Version = "1.0.0", Group = "blue",
        Description = "返回输入", TriggerCondition = "需要回显时", ParameterGuidance = "一个字符串参数"
    };

    private sealed class TestAppDataDirectoryProvider(string directoryPath) : IAppDataDirectoryProvider
    {
        public string DirectoryPath => directoryPath;
    }

    private sealed class TestRequestContextAccessor : IMcpRequestContextAccessor
    {
        public string GetRemoteAddress() => "127.0.0.1";
    }
}
