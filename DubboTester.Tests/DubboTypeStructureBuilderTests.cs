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
