using DubboNet.Clients.DataModle;
using DubboTester.Services;
using Xunit;

namespace DubboTester.Tests;

public sealed class NacosRegistryCatalogTests
{
    [Fact]
    public void ConvertProvider_PreservesDubboNetProviderMetadataForTreeAndDirectRouting()
    {
        DubboServiceEndPointInfo endpoint = DubboServiceEndPointInfo.GetDubboServiceEndPointInfo(
            new Uri(
                "tri://10.0.0.8:50051/com.example.DemoService"
                + "?interface=com.example.DemoService"
                + "&application=demo-provider"
                + "&methods=sayHello%2Cecho"
                + "&version=1.0.0"
                + "&group=blue"
                + "&weight=100"
                + "&disabled=false"));

        var provider = NacosRegistryCatalogClient.ConvertProvider(
            endpoint,
            "com.example.FallbackService");

        Assert.Equal("tri", provider.Scheme);
        Assert.Equal("10.0.0.8:50051", provider.Address);
        Assert.Equal("com.example.DemoService", provider.ServiceName);
        Assert.Equal(["sayHello", "echo"], provider.Methods);
        Assert.Equal(100, provider.Weight);
        Assert.False(provider.Disabled);
        Assert.Contains("interface=com.example.DemoService", provider.RegistryUrl);
    }
}
