namespace DubboTester.Models;

public enum JavaTypeKind
{
    Scalar,
    Object,
    Collection,
    Map,
    Enum,
    Unknown,
    RecursiveReference
}

public sealed class JavaTypeStructure
{
    public required string Name { get; init; }
    public required string JavaType { get; init; }
    public JavaTypeKind Kind { get; init; }
    public IReadOnlyList<string> EnumValues { get; init; } = [];
    public IReadOnlyList<JavaTypeStructure> Children { get; init; } = [];

    public bool IsStructured => Kind is JavaTypeKind.Object
        or JavaTypeKind.Collection
        or JavaTypeKind.Map
        or JavaTypeKind.Enum
        or JavaTypeKind.RecursiveReference;

    public string KindLabel => Kind switch
    {
        JavaTypeKind.Object => "对象",
        JavaTypeKind.Collection => "集合",
        JavaTypeKind.Map => "Map",
        JavaTypeKind.Enum => "枚举",
        JavaTypeKind.RecursiveReference => "递归引用",
        JavaTypeKind.Unknown => "未知",
        _ => "标量"
    };
}

public sealed class MethodParameterStructure
{
    public required int Index { get; init; }
    public required JavaTypeStructure Structure { get; init; }

    public string DisplayName => $"参数 {Index + 1}";
}
