using System.Text.Json;
using DubboNet.Clients.DataModle;
using DubboTester.Models;
using DubboTester.Services;
using Xunit;

namespace DubboTester.Tests;

public sealed class DubboTypeStructureBuilderTests
{
    [Fact]
    public void Build_ResolvesNestedPojoCollectionEnumAndRecursiveReference()
    {
        var signature = new MethodSignatureItem
        {
            ServiceName = "com.foo.DemoService",
            MethodName = "save",
            ParameterTypes = ["com.foo.ApplyEntityReq"],
            TypeDefinitions =
            [
                new DubboTypeMetadata
                {
                    Type = "com.foo.ApplyEntityReq",
                    TypeBuilderName = "DefaultTypeBuilder",
                    Properties = new Dictionary<string, DubboTypeMetadata>
                    {
                        ["id"] = Wrapper("java.lang.Long", "long"),
                        ["name"] = new()
                        {
                            Type = "java.lang.String",
                            TypeBuilderName = "DefaultTypeBuilder",
                            Properties = new Dictionary<string, DubboTypeMetadata>
                            {
                                ["value"] = new() { Type = "char[]", TypeBuilderName = "ArrayTypeBuilder" },
                                ["hash"] = new() { Type = "int", TypeBuilderName = "DefaultTypeBuilder" }
                            }
                        },
                        ["children"] = new()
                        {
                            Type = "java.util.List<com.foo.ChildReq>",
                            TypeBuilderName = "CollectionTypeBuilder"
                        },
                        ["status"] = new() { Type = "com.foo.Status" },
                        ["parent"] = new() { Type = "com.foo.ApplyEntityReq" }
                    }
                },
                new DubboTypeMetadata
                {
                    Type = "com.foo.ChildReq",
                    TypeBuilderName = "DefaultTypeBuilder",
                    Properties = new Dictionary<string, DubboTypeMetadata>
                    {
                        ["value"] = new() { Type = "java.lang.String" }
                    }
                },
                new DubboTypeMetadata
                {
                    Type = "com.foo.Status",
                    TypeBuilderName = "EnumTypeBuilder",
                    EnumValues = ["ENABLED", "DISABLED"]
                }
            ]
        };

        IReadOnlyList<MethodParameterStructure> parameters = DubboTypeStructureBuilder.Build(signature);
        JavaTypeStructure request = Assert.Single(parameters).Structure;

        Assert.Equal(JavaTypeKind.Object, request.Kind);
        Assert.Equal(JavaTypeKind.Collection, request.Children.Single(item => item.Name == "children").Kind);
        Assert.Equal(JavaTypeKind.Enum, request.Children.Single(item => item.Name == "status").Kind);
        Assert.Equal(JavaTypeKind.RecursiveReference, request.Children.Single(item => item.Name == "parent").Kind);
        Assert.Empty(request.Children.Single(item => item.Name == "id").Children);
        Assert.Empty(request.Children.Single(item => item.Name == "name").Children);

        using JsonDocument example = JsonDocument.Parse(DubboTypeStructureBuilder.GenerateExampleJson(parameters));
        JsonElement root = example.RootElement;
        Assert.Equal(0, root.GetProperty("id").GetInt32());
        Assert.Equal("string", root.GetProperty("name").GetString());
        Assert.Equal("ENABLED", root.GetProperty("status").GetString());
        Assert.Equal("string", root.GetProperty("children")[0].GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("parent").ValueKind);
        Assert.False(root.GetProperty("name").ValueKind == JsonValueKind.Object);
        Assert.False(root.TryGetProperty("hash", out _));
    }

    [Fact]
    public void GenerateExampleJson_WrapsSingleCollectionAsOneMethodArgument()
    {
        var signature = new MethodSignatureItem
        {
            ServiceName = "com.foo.DemoService",
            MethodName = "saveAll",
            ParameterTypes = ["java.util.List<java.lang.Long>"]
        };

        IReadOnlyList<MethodParameterStructure> parameters = DubboTypeStructureBuilder.Build(signature);
        string json = DubboTypeStructureBuilder.GenerateExampleJson(parameters);

        Assert.Equal("[[0]]", JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(json)));
        Assert.Single(JsonArgumentParser.ParseArguments(json));
    }

    [Fact]
    public void GenerateExampleJson_UsesTypesNormalizedFromOfficialDubboMetadata()
    {
        var signature = new MethodSignatureItem
        {
            ServiceName = "com.example.dubbonet.api.OrderService",
            MethodName = "calculateTotal",
            ParameterTypes = ["java.util.List<com.example.dubbonet.model.OrderItem>"],
            TypeDefinitions =
            [
                new DubboTypeMetadata
                {
                    Type = "java.util.List<com.example.dubbonet.model.OrderItem>",
                    Items = [new() { Type = "com.example.dubbonet.model.OrderItem" }]
                },
                new DubboTypeMetadata
                {
                    Type = "com.example.dubbonet.model.OrderItem",
                    Properties = new Dictionary<string, DubboTypeMetadata>
                    {
                        ["sku"] = new() { Type = "java.lang.String" },
                        ["name"] = new() { Type = "java.lang.String" },
                        ["quantity"] = new() { Type = "int" },
                        ["unitPrice"] = new() { Type = "java.math.BigDecimal" }
                    }
                }
            ]
        };

        IReadOnlyList<MethodParameterStructure> parameters = DubboTypeStructureBuilder.Build(signature);
        using JsonDocument example = JsonDocument.Parse(
            DubboTypeStructureBuilder.GenerateExampleJson(parameters));
        JsonElement item = example.RootElement[0][0];

        Assert.Equal("string", item.GetProperty("name").GetString());
        Assert.Equal(0, item.GetProperty("quantity").GetInt32());
        Assert.Equal("string", item.GetProperty("sku").GetString());
        Assert.Equal(0, item.GetProperty("unitPrice").GetInt32());
    }

    [Fact]
    public void GenerateExampleJson_GeneratesExamplesForMultipleScalarParameters()
    {
        var signature = new MethodSignatureItem
        {
            ServiceName = "com.foo.DemoService",
            MethodName = "analyze",
            ParameterTypes = ["java.lang.Long", "java.lang.String", "java.lang.Boolean"]
        };

        IReadOnlyList<MethodParameterStructure> parameters = DubboTypeStructureBuilder.Build(signature);
        string json = DubboTypeStructureBuilder.GenerateExampleJson(parameters);

        Assert.Equal("[0,\"string\",false]", JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(json)));
        Assert.Equal(3, JsonArgumentParser.ParseArguments(json).Length);
    }

    [Theory]
    [InlineData("java.lang.Integer", "0")]
    [InlineData("java.lang.String", "\"string\"")]
    [InlineData("boolean", "false")]
    public void GenerateExampleJson_GeneratesExampleForSingleScalarParameter(
        string javaType,
        string expectedJson)
    {
        var signature = new MethodSignatureItem
        {
            ServiceName = "com.foo.DemoService",
            MethodName = "find",
            ParameterTypes = [javaType]
        };

        IReadOnlyList<MethodParameterStructure> parameters = DubboTypeStructureBuilder.Build(signature);
        string json = DubboTypeStructureBuilder.GenerateExampleJson(parameters);

        Assert.Equal(expectedJson, json.Trim());
        Assert.Single(JsonArgumentParser.ParseArguments(json));
    }

    [Fact]
    public void GenerateExampleJson_DoesNotEmitJavaWrapperImplementationFields()
    {
        var signature = new MethodSignatureItem
        {
            ServiceName = "com.byai.decision.api.botlab.BotlabEntityWriteRemoteService",
            MethodName = "addAudit",
            ParameterTypes = ["com.byai.decision.api.botlab.request.ApplyEntityReq"],
            TypeDefinitions =
            [
                new DubboTypeMetadata
                {
                    Type = "com.byai.decision.api.botlab.request.ApplyEntityReq",
                    TypeBuilderName = "org.apache.dubbo.metadata.definition.builder.DefaultTypeBuilder",
                    Properties = new Dictionary<string, DubboTypeMetadata>
                    {
                        ["botEntityId"] = Wrapper("java.lang.Long", "long"),
                        ["remark"] = new()
                        {
                            Type = "java.lang.String",
                            TypeBuilderName = "org.apache.dubbo.metadata.definition.builder.DefaultTypeBuilder",
                            Properties = new Dictionary<string, DubboTypeMetadata>
                            {
                                ["value"] = new()
                                {
                                    Type = "char[]",
                                    TypeBuilderName = "org.apache.dubbo.metadata.definition.builder.ArrayTypeBuilder"
                                },
                                ["hash"] = new()
                                {
                                    Type = "int",
                                    TypeBuilderName = "org.apache.dubbo.metadata.definition.builder.DefaultTypeBuilder"
                                }
                            }
                        },
                        ["type"] = Wrapper("java.lang.Integer", "int")
                    }
                }
            ]
        };

        IReadOnlyList<MethodParameterStructure> parameters = DubboTypeStructureBuilder.Build(signature);
        using JsonDocument example = JsonDocument.Parse(DubboTypeStructureBuilder.GenerateExampleJson(parameters));
        JsonElement root = example.RootElement;

        Assert.Equal(3, root.EnumerateObject().Count());
        Assert.Equal(0, root.GetProperty("botEntityId").GetInt32());
        Assert.Equal("string", root.GetProperty("remark").GetString());
        Assert.Equal(0, root.GetProperty("type").GetInt32());
        Assert.False(root.TryGetProperty("value", out _));
        Assert.False(root.TryGetProperty("hash", out _));
    }

    [Fact]
    public void GenerateExampleJson_UsesDubboCompatibleJavaUtilDateFormat()
    {
        var signature = new MethodSignatureItem
        {
            ServiceName = "com.foo.DemoService",
            MethodName = "save",
            ParameterTypes = ["com.foo.Request"],
            TypeDefinitions =
            [
                new DubboTypeMetadata
                {
                    Type = "com.foo.Request",
                    TypeBuilderName = "DefaultTypeBuilder",
                    Properties = new Dictionary<string, DubboTypeMetadata>
                    {
                        ["gmtCreate"] = new()
                        {
                            Type = "java.util.Date",
                            TypeBuilderName = "DefaultTypeBuilder",
                            Properties = new Dictionary<string, DubboTypeMetadata>
                            {
                                ["fastTime"] = new() { Type = "long" }
                            }
                        }
                    }
                }
            ]
        };

        IReadOnlyList<MethodParameterStructure> parameters = DubboTypeStructureBuilder.Build(signature);
        using JsonDocument example = JsonDocument.Parse(DubboTypeStructureBuilder.GenerateExampleJson(parameters));

        Assert.Equal("2026-09-30 00:00:00", example.RootElement.GetProperty("gmtCreate").GetString());
    }

    [Fact]
    public void GenerateExampleJson_ResolvesDubboJsonPathReferencesForCollectionItems()
    {
        var signature = new MethodSignatureItem
        {
            ServiceName = "com.example.dubbonet.api.OrderService",
            MethodName = "calculateTotal",
            ParameterTypes = ["java.util.List<com.example.dubbonet.model.OrderItem>"],
            TypeDefinitions =
            [
                new DubboTypeMetadata
                {
                    Type = "com.example.dubbonet.model.Order",
                    Properties = new Dictionary<string, DubboTypeMetadata>
                    {
                        ["orderId"] = new() { Type = "java.lang.String" },
                        ["totalAmount"] = new() { Type = "java.math.BigDecimal" }
                    }
                },
                new DubboTypeMetadata
                {
                    Type = "com.example.dubbonet.model.PageResult",
                    Properties = new Dictionary<string, DubboTypeMetadata>
                    {
                        ["pageNo"] = new() { Type = "int" }
                    }
                },
                new DubboTypeMetadata
                {
                    Type = "com.example.dubbonet.model.OperationResult"
                },
                new DubboTypeMetadata
                {
                    Type = "com.example.dubbonet.model.OrderQuery",
                    Properties = new Dictionary<string, DubboTypeMetadata>
                    {
                        // This is the shape returned by an older DubboNet cache after the
                        // anonymous entries were moved: pageNo incorrectly points to itself.
                        ["pageNo"] = new() { Reference = "$.types[3].properties.pageNo" }
                    }
                },
                new DubboTypeMetadata
                {
                    Type = "com.example.dubbonet.model.OrderItem",
                    TypeBuilderName = "DefaultTypeBuilder",
                    Properties = new Dictionary<string, DubboTypeMetadata>
                    {
                        ["name"] = new() { Reference = "$.types[0].properties.orderId" },
                        ["quantity"] = new() { Reference = "$.types[3].properties.pageNo" },
                        ["sku"] = new() { Reference = "#/types/0/properties/orderId" },
                        ["unitPrice"] = new() { Reference = "$.types[0].properties.totalAmount" }
                    }
                }
            ]
        };

        IReadOnlyList<MethodParameterStructure> parameters = DubboTypeStructureBuilder.Build(signature);
        using JsonDocument example = JsonDocument.Parse(DubboTypeStructureBuilder.GenerateExampleJson(parameters));
        JsonElement item = example.RootElement[0][0];

        Assert.Equal("string", item.GetProperty("name").GetString());
        Assert.Equal(0, item.GetProperty("quantity").GetInt32());
        Assert.Equal("string", item.GetProperty("sku").GetString());
        Assert.Equal(0, item.GetProperty("unitPrice").GetInt32());
    }

    private static DubboTypeMetadata Wrapper(string type, string valueType) => new()
    {
        Type = type,
        TypeBuilderName = "org.apache.dubbo.metadata.definition.builder.DefaultTypeBuilder",
        Properties = new Dictionary<string, DubboTypeMetadata>
        {
            ["value"] = new()
            {
                Type = valueType,
                TypeBuilderName = "org.apache.dubbo.metadata.definition.builder.DefaultTypeBuilder"
            }
        }
    };
}
