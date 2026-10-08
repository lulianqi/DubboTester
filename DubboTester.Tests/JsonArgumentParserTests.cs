using DubboTester.Services;
using NHessian.IO;
using Xunit;

namespace DubboTester.Tests;

public sealed class JsonArgumentParserTests
{
    [Fact]
    public void ParseArguments_UsesHessianSupportedDoubleForFractionalNumbers()
    {
        object?[] arguments = JsonArgumentParser.ParseArguments("""
            [
              [
                {
                  "sku": "SKU-A",
                  "name": "mouse",
                  "quantity": 2,
                  "unitPrice": 79.90
                }
              ]
            ]
            """);

        var items = Assert.IsAssignableFrom<IReadOnlyList<object?>>(Assert.Single(arguments));
        var item = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(Assert.Single(items));

        Assert.IsType<int>(item["quantity"]);
        double unitPrice = Assert.IsType<double>(item["unitPrice"]);
        Assert.Equal(79.90d, unitPrice);
        Assert.DoesNotContain(item.Values, value => value is decimal);
    }

    [Fact]
    public void ParseArguments_FractionalDtoListCanBeSerializedByNHessian()
    {
        object?[] arguments = JsonArgumentParser.ParseArguments("""
            [
              [
                {
                  "sku": "SKU-A",
                  "name": "mouse",
                  "quantity": 2,
                  "unitPrice": 79.90
                }
              ]
            ]
            """);

        using var stream = new MemoryStream();
        using var writer = new HessianStreamWriter(stream, leaveOpen: true);
        var output = new HessianOutputV2(writer, TypeBindings.Java);

        output.WriteObject(arguments);

        Assert.NotEmpty(stream.ToArray());
    }

    [Fact]
    public void ParseJavaTypes_TreatsGenericMapAsOneErasedParameter()
    {
        string[] types = JsonArgumentParser.ParseJavaTypes(
            "java.util.Map<java.lang.String, java.lang.String>");
        object?[] arguments = JsonArgumentParser.ParseArguments("""
            { "key": "string" }
            """);

        Assert.Equal(["java.util.Map"], types);
        Assert.Single(arguments);
        Assert.Equal(arguments.Length, types.Length);
    }

    [Fact]
    public void ParseJavaTypes_SplitsOnlyTopLevelCommasInNestedGenerics()
    {
        string[] types = JsonArgumentParser.ParseJavaTypes(
            "java.util.Map<java.lang.String, java.util.List<java.lang.Integer>>, " +
            "java.util.List<com.foo.Request>, java.lang.Long[]");

        Assert.Equal(
            ["java.util.Map", "java.util.List", "java.lang.Long[]"],
            types);
    }

    [Theory]
    [InlineData("java.lang.String, java.lang.Long", new[] { "java.lang.String", "java.lang.Long" })]
    [InlineData("java.lang.String...", new[] { "java.lang.String[]" })]
    [InlineData("  ", new string[0])]
    public void ParseJavaTypes_PreservesOrdinaryTypesAndNormalizesVarArgs(
        string declaration,
        string[] expected)
    {
        Assert.Equal(expected, JsonArgumentParser.ParseJavaTypes(declaration));
    }
}
