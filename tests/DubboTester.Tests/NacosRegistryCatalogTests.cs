using DubboTester.Services;
using Nacos.V2.Naming.Dtos;
using Xunit;

namespace DubboTester.Tests;

public sealed class NacosRegistryCatalogTests
{
    [Theory]
    [InlineData("providers:com.example.DemoService:1.0.0:blue", "com.example.DemoService")]
    [InlineData("providers:com.example.DemoService", "com.example.DemoService")]
    [InlineData("com.example.DemoService", "com.example.DemoService")]
    [InlineData("ordinary-web-service", null)]
    public void ExtractInterfaceName_RecognizesDubboNacosServiceNames(
        string serviceName,
        string? expected)
    {
        Assert.Equal(expected, NacosRegistryCatalogClient.ExtractInterfaceName(serviceName));
    }

    [Fact]
    public void ConvertInstance_PreservesProviderMetadataForTreeAndDirectRouting()
    {
        var instance = new Instance
        {
            Ip = "10.0.0.8",
            Port = 50051,
            Healthy = true,
            Enabled = true,
            Ephemeral = true,
            Weight = 1,
            Metadata = new Dictionary<string, string>
            {
                ["protocol"] = "tri",
                ["interface"] = "com.example.DemoService",
                ["application"] = "demo-provider",
                ["methods"] = "sayHello,echo",
                ["version"] = "1.0.0",
                ["group"] = "blue"
            }
        };

        var provider = NacosRegistryCatalogClient.ConvertInstance(
            instance,
            "com.example.FallbackService");

        Assert.NotNull(provider);
        Assert.Equal("tri", provider.Scheme);
        Assert.Equal("10.0.0.8:50051", provider.Address);
        Assert.Equal("com.example.DemoService", provider.ServiceName);
        Assert.Equal(["sayHello", "echo"], provider.Methods);
        Assert.Equal(100, provider.Weight);
        Assert.False(provider.Disabled);
        Assert.Contains("interface=com.example.DemoService", provider.RegistryUrl);
    }
}
