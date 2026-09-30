using System.Text.Encodings.Web;
using System.Text.Json;
using DubboNet.Clients.DataModle;
using DubboTester.Models;

namespace DubboTester.Services;

public static class DubboTypeStructureBuilder
{
    private const int MaximumDepth = 12;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly HashSet<string> StringTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "char", "java.lang.Character", "java.lang.String", "java.lang.CharSequence",
        "java.lang.StringBuilder", "java.lang.StringBuffer", "java.lang.Class",
        "java.util.UUID", "java.net.URI", "java.net.URL"
    };

    private static readonly HashSet<string> BooleanTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "boolean", "java.lang.Boolean"
    };

    private static readonly HashSet<string> NumberTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "byte", "short", "int", "long", "float", "double",
        "java.lang.Byte", "java.lang.Short", "java.lang.Integer", "java.lang.Long",
        "java.lang.Float", "java.lang.Double", "java.math.BigDecimal", "java.math.BigInteger"
    };

    private static readonly HashSet<string> DateTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "java.util.Date", "java.sql.Date", "java.sql.Timestamp", "java.time.Instant",
        "java.time.LocalDate", "java.time.LocalDateTime", "java.time.OffsetDateTime",
        "java.time.ZonedDateTime"
    };

    public static IReadOnlyList<MethodParameterStructure> Build(MethodSignatureItem signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        var context = new BuildContext(signature.TypeDefinitions);
        return signature.ParameterTypes
            .Select((type, index) => new MethodParameterStructure
            {
                Index = index,
                Structure = context.Build($"arg{index + 1}", type, null, [], 0)
            })
            .ToArray();
    }

    public static bool HasDetailedStructure(IEnumerable<MethodParameterStructure> parameters) =>
        parameters.Any(parameter => parameter.Structure.IsStructured);

    public static string GenerateExampleJson(IReadOnlyList<MethodParameterStructure> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        object? value = parameters.Count switch
        {
            0 => Array.Empty<object>(),
            1 when parameters[0].Structure.Kind == JavaTypeKind.Collection =>
                new[] { BuildExample(parameters[0].Structure) },
            1 => BuildExample(parameters[0].Structure),
            _ => parameters.Select(parameter => BuildExample(parameter.Structure)).ToArray()
        };
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    private static object? BuildExample(JavaTypeStructure node) => node.Kind switch
    {
        JavaTypeKind.Object => node.Children.ToDictionary(
            child => child.Name,
            BuildExample,
            StringComparer.Ordinal),
        JavaTypeKind.Collection => new[]
        {
            node.Children.Count > 0 ? BuildExample(node.Children[0]) : null
        },
        JavaTypeKind.Map => new Dictionary<string, object?>
        {
            ["key"] = node.Children.Count > 0 ? BuildExample(node.Children[0]) : null
        },
        JavaTypeKind.Enum => node.EnumValues.FirstOrDefault() ?? "ENUM_VALUE",
        JavaTypeKind.RecursiveReference => null,
        _ => ScalarExample(node.JavaType)
    };

    private static object? ScalarExample(string javaType)
    {
        string type = NormalizeType(javaType);
        if (StringTypes.Contains(type)) return "string";
        if (BooleanTypes.Contains(type)) return false;
        if (NumberTypes.Contains(type)) return 0;
        if (type is "java.util.Date" or "java.sql.Timestamp" or "java.time.LocalDateTime")
            return "2026-09-30 00:00:00";
        if (type is "java.sql.Date" or "java.time.LocalDate") return "2026-09-30";
        if (DateTypes.Contains(type)) return "2026-09-30T00:00:00+08:00";

        return type is "void" or "java.lang.Void" ? null : new Dictionary<string, object?>();
    }

    private sealed class BuildContext
    {
        private readonly IReadOnlyList<DubboTypeMetadata> _definitions;
        private readonly Dictionary<string, DubboTypeMetadata> _definitionsByName;

        public BuildContext(IReadOnlyList<DubboTypeMetadata> definitions)
        {
            _definitions = definitions ?? [];
            _definitionsByName = new Dictionary<string, DubboTypeMetadata>(StringComparer.Ordinal);
            foreach (DubboTypeMetadata definition in _definitions)
            {
                AddDefinitionName(definition.Id, definition);
                AddDefinitionName(definition.Type, definition);
            }
        }

        public JavaTypeStructure Build(
            string name,
            string? declaredType,
            DubboTypeMetadata? inline,
            HashSet<string> ancestors,
            int depth)
        {
            string javaType = EffectiveType(declaredType, inline);
            DubboTypeMetadata? definition = Resolve(inline, javaType);
            if (definition is not null && !string.IsNullOrWhiteSpace(inline?.Reference))
            {
                javaType = DefinitionType(definition, javaType);
            }
            string identity = DefinitionIdentity(definition, javaType);

            if (depth >= MaximumDepth || (!string.IsNullOrWhiteSpace(identity) && ancestors.Contains(identity)))
            {
                return new JavaTypeStructure
                {
                    Name = name,
                    JavaType = javaType,
                    Kind = JavaTypeKind.RecursiveReference
                };
            }

            var nextAncestors = new HashSet<string>(ancestors, StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(identity)) nextAncestors.Add(identity);

            IReadOnlyList<string> enumValues = definition?.EnumValues?.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray()
                ?? inline?.EnumValues?.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray()
                ?? [];
            if (enumValues.Count > 0 || IsEnum(definition, inline))
            {
                return new JavaTypeStructure
                {
                    Name = name,
                    JavaType = javaType,
                    Kind = JavaTypeKind.Enum,
                    EnumValues = enumValues
                };
            }

            if (IsArray(javaType) || IsCollection(javaType, definition, inline))
            {
                DubboTypeMetadata? item = FirstItem(definition, inline);
                string itemType = item is null ? FirstGenericArgument(javaType) ?? ArrayElementType(javaType) ?? "java.lang.Object" : EffectiveType(null, item);
                return new JavaTypeStructure
                {
                    Name = name,
                    JavaType = javaType,
                    Kind = JavaTypeKind.Collection,
                    Children = [Build("元素", itemType, item, nextAncestors, depth + 1)]
                };
            }

            if (IsMap(javaType, definition, inline))
            {
                DubboTypeMetadata? valueDefinition = MapValueItem(definition, inline);
                string valueType = valueDefinition is null ? GenericArguments(javaType).Skip(1).FirstOrDefault() ?? "java.lang.Object" : EffectiveType(null, valueDefinition);
                return new JavaTypeStructure
                {
                    Name = name,
                    JavaType = javaType,
                    Kind = JavaTypeKind.Map,
                    Children = [Build("值", valueType, valueDefinition, nextAncestors, depth + 1)]
                };
            }

            // Dubbo 2.7.x's DefaultTypeBuilder recursively reports implementation fields
            // for JDK scalar/wrapper classes as well. For example, String exposes value/hash
            // and Long/Integer expose value. Those fields describe the JDK implementation;
            // they are not members of the business request DTO and must never be emitted.
            if (IsKnownScalar(javaType))
            {
                return new JavaTypeStructure
                {
                    Name = name,
                    JavaType = javaType,
                    Kind = JavaTypeKind.Scalar
                };
            }

            Dictionary<string, DubboTypeMetadata> properties = definition?.Properties
                ?? inline?.Properties
                ?? new Dictionary<string, DubboTypeMetadata>();
            if (properties.Count > 0 || IsObjectDefinition(definition, inline))
            {
                return new JavaTypeStructure
                {
                    Name = name,
                    JavaType = javaType,
                    Kind = JavaTypeKind.Object,
                    Children = properties
                        .OrderBy(property => property.Key, StringComparer.Ordinal)
                        .Select(property => Build(
                            property.Key,
                            EffectiveType(null, property.Value),
                            property.Value,
                            nextAncestors,
                            depth + 1))
                        .ToArray()
                };
            }

            return new JavaTypeStructure { Name = name, JavaType = javaType, Kind = JavaTypeKind.Unknown };
        }

        private void AddDefinitionName(string? name, DubboTypeMetadata definition)
        {
            if (!string.IsNullOrWhiteSpace(name)) _definitionsByName.TryAdd(NormalizeReference(name), definition);
        }

        private DubboTypeMetadata? Resolve(DubboTypeMetadata? inline, string javaType)
        {
            if (inline is not null)
            {
                DubboTypeMetadata? referenced = ResolveReference(inline.Reference);
                if (referenced is not null) return referenced;
                if ((inline.Properties?.Count ?? 0) > 0
                    || (inline.Items?.Count ?? 0) > 0
                    || (inline.EnumValues?.Count ?? 0) > 0)
                {
                    return inline;
                }
            }

            _definitionsByName.TryGetValue(NormalizeReference(javaType), out DubboTypeMetadata? definition);
            return definition;
        }

        private DubboTypeMetadata? ResolveReference(string? reference)
        {
            if (string.IsNullOrWhiteSpace(reference)) return null;
            string normalized = NormalizeReference(reference);
            if (_definitionsByName.TryGetValue(normalized, out DubboTypeMetadata? named)) return named;

            string tail = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
            if (int.TryParse(tail, out int index) && index >= 0 && index < _definitions.Count) return _definitions[index];
            return _definitionsByName.GetValueOrDefault(tail);
        }
    }

    private static string EffectiveType(string? declaredType, DubboTypeMetadata? metadata)
    {
        if (!string.IsNullOrWhiteSpace(declaredType)) return NormalizeType(declaredType);
        if (!string.IsNullOrWhiteSpace(metadata?.Type) && !IsCategory(metadata.Type)) return NormalizeType(metadata.Type);
        if (!string.IsNullOrWhiteSpace(metadata?.Id)) return NormalizeType(metadata.Id);
        if (!string.IsNullOrWhiteSpace(metadata?.Reference)) return NormalizeReference(metadata.Reference);
        return NormalizeType(metadata?.Type ?? "java.lang.Object");
    }

    private static string DefinitionIdentity(DubboTypeMetadata? definition, string javaType) =>
        NormalizeReference(definition?.Id ?? (IsCategory(definition?.Type) ? javaType : definition?.Type) ?? javaType);

    private static DubboTypeMetadata? FirstItem(DubboTypeMetadata? definition, DubboTypeMetadata? inline) =>
        definition?.Items?.FirstOrDefault() ?? inline?.Items?.FirstOrDefault();

    private static DubboTypeMetadata? MapValueItem(DubboTypeMetadata? definition, DubboTypeMetadata? inline)
    {
        IReadOnlyList<DubboTypeMetadata> items = definition?.Items ?? inline?.Items ?? [];
        return items.Count > 1 ? items[1] : items.FirstOrDefault();
    }

    private static string DefinitionType(DubboTypeMetadata definition, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(definition.Id)) return NormalizeType(definition.Id);
        if (!string.IsNullOrWhiteSpace(definition.Type) && !IsCategory(definition.Type)) return NormalizeType(definition.Type);
        return fallback;
    }

    private static bool IsKnownScalar(string javaType)
    {
        string type = NormalizeType(javaType);
        return StringTypes.Contains(type)
            || BooleanTypes.Contains(type)
            || NumberTypes.Contains(type)
            || DateTypes.Contains(type)
            || type is "void" or "java.lang.Void" or "java.lang.Object";
    }

    private static bool IsArray(string javaType) => NormalizeType(javaType).EndsWith("[]", StringComparison.Ordinal);
    private static bool IsCollection(string javaType, params DubboTypeMetadata?[] metadata) =>
        IsGenericType(javaType, "List", "Set", "Collection", "Iterable", "Queue", "Deque")
        || metadata.Any(item => ContainsBuilder(item, "Collection") || ContainsBuilder(item, "Array"));

    private static bool IsMap(string javaType, params DubboTypeMetadata?[] metadata) =>
        IsGenericType(javaType, "Map", "Dictionary") || metadata.Any(item => ContainsBuilder(item, "Map"));

    private static bool IsEnum(params DubboTypeMetadata?[] metadata) =>
        metadata.Any(item => ContainsBuilder(item, "Enum") || string.Equals(item?.Type, "enum", StringComparison.OrdinalIgnoreCase));

    private static bool IsObjectDefinition(params DubboTypeMetadata?[] metadata) =>
        metadata.Any(item => ContainsBuilder(item, "Default")
            || string.Equals(item?.Type, "object", StringComparison.OrdinalIgnoreCase));

    private static bool ContainsBuilder(DubboTypeMetadata? metadata, string value) =>
        metadata?.TypeBuilderName?.Contains(value, StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsGenericType(string javaType, params string[] names)
    {
        string raw = NormalizeType(javaType);
        int genericIndex = raw.IndexOf('<');
        if (genericIndex >= 0) raw = raw[..genericIndex];
        string simpleName = raw.Split('.').LastOrDefault() ?? raw;
        return names.Any(name => simpleName.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static string? FirstGenericArgument(string javaType) => GenericArguments(javaType).FirstOrDefault();

    private static IReadOnlyList<string> GenericArguments(string javaType)
    {
        string value = NormalizeType(javaType);
        int start = value.IndexOf('<');
        int end = value.LastIndexOf('>');
        if (start < 0 || end <= start) return [];

        var result = new List<string>();
        int nested = 0;
        int segmentStart = start + 1;
        for (int index = start + 1; index < end; index++)
        {
            switch (value[index])
            {
                case '<': nested++; break;
                case '>': nested--; break;
                case ',' when nested == 0:
                    result.Add(value[segmentStart..index].Trim());
                    segmentStart = index + 1;
                    break;
            }
        }
        result.Add(value[segmentStart..end].Trim());
        return result.Where(item => item.Length > 0).ToArray();
    }

    private static string? ArrayElementType(string javaType)
    {
        string type = NormalizeType(javaType);
        return type.EndsWith("[]", StringComparison.Ordinal) ? type[..^2] : null;
    }

    private static bool IsCategory(string? value) => value is not null && value.ToLowerInvariant() is
        "object" or "array" or "collection" or "map" or "enum" or "string" or "number" or "boolean";

    private static string NormalizeType(string value) => value.Trim().Replace("$", ".", StringComparison.Ordinal);

    private static string NormalizeReference(string value) => NormalizeType(value)
        .Replace("#/definitions/", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace("#/types/", string.Empty, StringComparison.OrdinalIgnoreCase);
}
