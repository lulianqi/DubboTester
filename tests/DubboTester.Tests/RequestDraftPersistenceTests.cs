using System.Text.Json;
using DubboNet.Clients;
using DubboNet.Clients.DataModle;
using DubboTester.Models;
using DubboTester.Services;
using Xunit;

namespace DubboTester.Tests;

public sealed class RequestDraftPersistenceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void PressureLoadBalanceMode_RoundTripsIndependentlyOfHomeSettings()
    {
        foreach (DubboClient.LoadBalanceMode mode in Enum.GetValues<DubboClient.LoadBalanceMode>())
        {
            var homeSettings = new TesterSettings();
            var store = new PressureTestDraftStore();
            store.Capture(homeSettings, new InvocationInput());
            Assert.True(store.TryTake(out TesterSettings pressureSettings, out _));
            pressureSettings.LoadBalanceMode = mode;

            string json = JsonSerializer.Serialize(new PressureTestPageDraft { Settings = pressureSettings }, JsonOptions);
            PressureTestPageDraft restored = JsonSerializer.Deserialize<PressureTestPageDraft>(json, JsonOptions)!;
            var runSettings = restored.Settings!.Clone();

            Assert.Equal(mode, restored.Settings.LoadBalanceMode);
            Assert.Equal(mode, runSettings.LoadBalanceMode);
            Assert.Equal(DubboClient.LoadBalanceMode.Random, homeSettings.LoadBalanceMode);
        }
    }

    [Fact]
    public void HomeDraft_RoundTripsRequestAndCachedSignatureWithoutInvokingService()
    {
        var draft = new HomeRequestDraft
        {
            Invocation = new InvocationInput
            {
                Route = InvocationRoute.DirectProvider,
                Host = "127.0.0.1",
                Port = 20881,
                RegistryRootPath = "/home-registry",
                ServiceName = "com.foo.UserService",
                MethodName = "save",
                JavaParameterTypes = "com.foo.User",
                Version = "home-version",
                Group = "home-group",
                RequestJson = "[{\"name\":\"draft\"}]",
                AttachmentsJson = "{\"traceId\":\"home\"}"
            },
            Signatures =
            [
                new MethodSignatureItem
                {
                    ServiceName = "com.foo.UserService",
                    MethodName = "save",
                    ParameterTypes = ["com.foo.User"],
                    ReturnType = "boolean",
                    Source = "MetadataCenter",
                    TypeDefinitions =
                    [
                        new DubboTypeMetadata
                        {
                            Type = "com.foo.User",
                            Properties = new Dictionary<string, DubboTypeMetadata>
                            {
                                ["name"] = new() { Type = "java.lang.String" }
                            }
                        }
                    ]
                }
            ]
        };

        string json = JsonSerializer.Serialize(draft, JsonOptions);
        HomeRequestDraft restored = JsonSerializer.Deserialize<HomeRequestDraft>(json, JsonOptions)!;

        Assert.Equal(draft.Invocation.RequestJson, restored.Invocation.RequestJson);
        Assert.Equal(draft.Invocation.JavaParameterTypes, restored.Invocation.JavaParameterTypes);
        Assert.Equal(draft.Invocation.Group, restored.Invocation.Group);
        Assert.Equal(draft.Invocation.Version, restored.Invocation.Version);
        Assert.Equal(draft.Invocation.AttachmentsJson, restored.Invocation.AttachmentsJson);
        Assert.Equal(InvocationRoute.DirectProvider, restored.Invocation.Route);
        Assert.Equal(20881, restored.Invocation.Port);
        Assert.Equal("/home-registry", restored.Invocation.RegistryRootPath);
        Assert.Equal("MetadataCenter", restored.Signatures[0].Source);
        Assert.Equal("boolean save(com.foo.User)", restored.Signatures[0].Signature);
        Assert.NotEmpty(DubboTypeStructureBuilder.Build(restored.Signatures[0]));
    }

    [Fact]
    public void PressureHandoff_IsOneWayAndConsumedOnce()
    {
        var store = new PressureTestDraftStore();
        var homeSettings = new TesterSettings { RegistryAddress = "home:2181", RegistryRootPath = "/home" };
        var homeInput = new InvocationInput { ServiceName = "HomeService", MethodName = "home", RequestJson = "[1]" };
        store.Capture(homeSettings, homeInput);

        Assert.True(store.TryTake(out TesterSettings pressureSettings, out InvocationInput pressureInput));
        pressureSettings.RegistryAddress = "pressure:2181";
        pressureInput.ServiceName = "PressureService";
        pressureInput.RequestJson = "[999]";

        Assert.Equal("home:2181", homeSettings.RegistryAddress);
        Assert.Equal("HomeService", homeInput.ServiceName);
        Assert.Equal("[1]", homeInput.RequestJson);
        Assert.False(store.TryTake(out _, out _));
    }

    [Fact]
    public void PressureDraft_PersistsOwnConnectionSettingsWithBackwardCompatibleDefaults()
    {
        var draft = new PressureTestPageDraft
        {
            Settings = new TesterSettings { RegistryAddress = "pressure:2181", RegistryRootPath = "/pressure" },
            Invocation = new InvocationInput { ServiceName = "PressureService", RequestJson = "[999]" },
            UserCount = 42
        };
        string json = JsonSerializer.Serialize(draft, JsonOptions);
        PressureTestPageDraft restored = JsonSerializer.Deserialize<PressureTestPageDraft>(json, JsonOptions)!;

        Assert.Equal("pressure:2181", restored.Settings!.RegistryAddress);
        Assert.Equal("/pressure", restored.Settings.RegistryRootPath);
        Assert.Equal("PressureService", restored.Invocation.ServiceName);
        Assert.Equal(42, restored.UserCount);
        Assert.Null(JsonSerializer.Deserialize<PressureTestPageDraft>("{}", JsonOptions)!.Settings);
    }
}
