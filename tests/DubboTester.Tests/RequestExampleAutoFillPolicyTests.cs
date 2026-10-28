using System.Text.Json;
using DubboNet.Clients.DataModle;
using DubboTester.Models;
using DubboTester.Services;
using Xunit;

namespace DubboTester.Tests;

public sealed class RequestExampleAutoFillPolicyTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" \n\t")]
    public void EmptyInput_WithMetadataAndParameters_IsFilled(string empty)
    {
        InvocationInput input = CreateInput(empty);

        Assert.True(RequestExampleAutoFillPolicy.TryFill(input, CreateSignature(), true, false));
        Assert.Equal("string", JsonSerializer.Deserialize<string>(input.RequestJson));
    }

    [Theory]
    [InlineData("[123]")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("user input being edited")]
    public void ExistingInputOrRestoredDraft_IsNeverOverwritten(string body)
    {
        InvocationInput input = CreateInput(body);

        Assert.False(RequestExampleAutoFillPolicy.TryFill(input, CreateSignature(), true, false));
        Assert.Equal(body, input.RequestJson);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Telnet")]
    [InlineData("ProviderTelnet")]
    public void WithoutMetadataCenter_DoesNotFill(string? source)
    {
        InvocationInput input = CreateInput();

        Assert.False(RequestExampleAutoFillPolicy.TryFill(input, CreateSignature(source), true, false));
        Assert.Equal(string.Empty, input.RequestJson);
    }

    [Fact]
    public void DisabledFailedMissingAndNoArgumentSignatures_DoNotFill()
    {
        InvocationInput input = CreateInput();
        MethodSignatureItem signature = CreateSignature();

        Assert.False(RequestExampleAutoFillPolicy.TryFill(input, signature, false, false));
        Assert.False(RequestExampleAutoFillPolicy.TryFill(input, signature, true, true));
        Assert.False(RequestExampleAutoFillPolicy.TryFill(input, null, true, false));
        signature = new MethodSignatureItem
        {
            ServiceName = input.ServiceName, MethodName = input.MethodName,
            Source = "MetadataCenter", ParameterTypes = []
        };
        input.JavaParameterTypes = string.Empty;
        Assert.False(RequestExampleAutoFillPolicy.TryFill(input, signature, true, false));
        Assert.Equal(string.Empty, input.RequestJson);
    }

    [Fact]
    public void StaleServiceMethodOrParameterTypes_DoNotFill()
    {
        InvocationInput input = CreateInput();
        MethodSignatureItem signature = CreateSignature();
        input.ServiceName = "com.foo.OtherService";
        Assert.False(RequestExampleAutoFillPolicy.TryFill(input, signature, true, false));
        input = CreateInput();
        input.MethodName = "other";
        Assert.False(RequestExampleAutoFillPolicy.TryFill(input, signature, true, false));
        input = CreateInput();
        input.JavaParameterTypes = "int";
        Assert.False(RequestExampleAutoFillPolicy.TryFill(input, signature, true, false));
    }

    [Fact]
    public void StructuredExample_UsesMetadataFields()
    {
        InvocationInput input = CreateInput();
        input.JavaParameterTypes = "com.foo.User";
        var signature = new MethodSignatureItem
        {
            ServiceName = input.ServiceName, MethodName = input.MethodName,
            Source = "MetadataCenter", ParameterTypes = ["com.foo.User"],
            TypeDefinitions =
            [
                new DubboTypeMetadata
                {
                    Type = "com.foo.User",
                    Properties = new Dictionary<string, DubboTypeMetadata>
                    {
                        ["name"] = new() { Type = "java.lang.String" },
                        ["age"] = new() { Type = "int" }
                    }
                }
            ]
        };

        Assert.True(RequestExampleAutoFillPolicy.TryFill(input, signature, true, false));
        using JsonDocument example = JsonDocument.Parse(input.RequestJson);
        Assert.Equal("string", example.RootElement.GetProperty("name").GetString());
        Assert.Equal(0, example.RootElement.GetProperty("age").GetInt32());
    }

    [Theory]
    [InlineData("java.util.List<com.foo.OrderItem>")]
    [InlineData("java.util.List")]
    public void GenericCollection_FillsUsingMetadataElementStructure(string inputType)
    {
        InvocationInput input = CreateInput();
        input.JavaParameterTypes = inputType;
        MethodSignatureItem signature = CreateOrderSignature(["java.util.List<com.foo.OrderItem>"]);

        Assert.True(RequestExampleAutoFillPolicy.TryFill(input, signature, true, false));
        using JsonDocument example = JsonDocument.Parse(input.RequestJson);
        JsonElement item = example.RootElement[0][0];
        Assert.Equal(0, item.GetProperty("quantity").GetInt32());
        Assert.Equal(0, item.GetProperty("price").GetInt32());
    }

    [Theory]
    [InlineData("java.util.Map<java.lang.String, java.util.List<com.foo.OrderItem>>, int")]
    [InlineData("java.util.Map, int")]
    public void NestedGenericMapAndMultipleArguments_FillUsingMetadataStructure(string inputTypes)
    {
        InvocationInput input = CreateInput();
        input.JavaParameterTypes = inputTypes;
        MethodSignatureItem signature = CreateOrderSignature(
            ["java.util.Map<java.lang.String, java.util.List<com.foo.OrderItem>>", "int"]);

        Assert.True(RequestExampleAutoFillPolicy.TryFill(input, signature, true, false));
        using JsonDocument example = JsonDocument.Parse(input.RequestJson);
        Assert.Equal(0, example.RootElement[0].GetProperty("key")[0].GetProperty("quantity").GetInt32());
        Assert.Equal(0, example.RootElement[1].GetInt32());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Setting_DefaultsOnAndPreservesSavedChoice(bool enabled)
    {
        Assert.True(new TesterSettings().AutoFillRequestExample);
        Assert.True(JsonSerializer.Deserialize<TesterSettings>("{}")!.AutoFillRequestExample);
        var settings = new TesterSettings { AutoFillRequestExample = enabled };
        TesterSettings restored = JsonSerializer.Deserialize<TesterSettings>(JsonSerializer.Serialize(settings))!;

        Assert.Equal(enabled, settings.Clone().AutoFillRequestExample);
        Assert.Equal(enabled, restored.AutoFillRequestExample);
    }

    private static InvocationInput CreateInput(string body = "") => new()
    {
        ServiceName = "com.foo.DemoService", MethodName = "echo",
        JavaParameterTypes = "java.lang.String", RequestJson = body
    };

    private static MethodSignatureItem CreateSignature(string? source = "MetadataCenter") => new()
    {
        ServiceName = "com.foo.DemoService", MethodName = "echo",
        ParameterTypes = ["java.lang.String"], Source = source
    };

    private static MethodSignatureItem CreateOrderSignature(string[] parameterTypes) => new()
    {
        ServiceName = "com.foo.DemoService", MethodName = "echo",
        ParameterTypes = parameterTypes, Source = "MetadataCenter",
        TypeDefinitions =
        [
            new DubboTypeMetadata
            {
                Type = "com.foo.OrderItem",
                Properties = new Dictionary<string, DubboTypeMetadata>
                {
                    ["quantity"] = new() { Type = "int" },
                    ["price"] = new() { Type = "java.math.BigDecimal" }
                }
            }
        ]
    };
}
