// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using ApiExtractor.Contracts;

namespace ApiExtractor.Java;

/// <summary>Root container for extracted Java API.</summary>
public sealed record ApiIndex : IApiIndex
{
    [JsonPropertyName("package")]
    public string Package { get; init; } = "";

    [JsonPropertyName("packages")]
    public IReadOnlyList<PackageInfo> Packages { get; init; } = [];

    /// <summary>Types from external dependencies that are referenced in the public API.</summary>
    [JsonPropertyName("dependencies")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<DependencyInfo>? Dependencies { get; init; }

    /// <summary>Gets all classes in the API.</summary>
    public IEnumerable<ClassInfo> GetAllClasses() =>
        Packages.SelectMany(p => p.Classes ?? []);

    /// <summary>Gets all types (classes + interfaces + annotations) in the API.</summary>
    public IEnumerable<ClassInfo> GetAllTypes() =>
        Packages.SelectMany(p => (p.Classes ?? []).Concat(p.Interfaces ?? []).Concat(p.Annotations ?? []));

    /// <summary>Gets all interfaces in the API.</summary>
    public IEnumerable<ClassInfo> GetAllInterfaces() =>
        Packages.SelectMany(p => p.Interfaces ?? []);

    /// <summary>Gets client classes (entry points for SDK operations).</summary>
    public IEnumerable<ClassInfo> GetClientClasses() =>
        GetAllClasses().Where(c => c.IsClientType);

    public string ToJson(bool pretty = false) => pretty
        ? JsonSerializer.Serialize(this, SourceGenerationContext.Indented.ApiIndex)
        : JsonSerializer.Serialize(this, SourceGenerationContext.Default.ApiIndex);

    public string ToStubs() => JavaFormatter.Format(this);
}

/// <summary>Information about types from a dependency package.</summary>
public sealed record DependencyInfo
{
    /// <summary>The package/group ID (e.g., "com.azure:azure-core").</summary>
    [JsonPropertyName("package")]
    public string Package { get; init; } = "";

    /// <summary>Classes from this dependency that are referenced in the API.</summary>
    [JsonPropertyName("classes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ClassInfo>? Classes { get; init; }

    /// <summary>Interfaces from this dependency that are referenced in the API.</summary>
    [JsonPropertyName("interfaces")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ClassInfo>? Interfaces { get; init; }

    /// <summary>Enums from this dependency that are referenced in the API.</summary>
    [JsonPropertyName("enums")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<EnumInfo>? Enums { get; init; }
}

/// <summary>A Java package containing types.</summary>
public sealed record PackageInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("classes")]
    public IReadOnlyList<ClassInfo>? Classes { get; init; }

    [JsonPropertyName("interfaces")]
    public IReadOnlyList<ClassInfo>? Interfaces { get; init; }

    [JsonPropertyName("enums")]
    public IReadOnlyList<EnumInfo>? Enums { get; init; }

    [JsonPropertyName("annotations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ClassInfo>? Annotations { get; init; }
}

/// <summary>A class, interface, record, or annotation type.</summary>
public sealed record ClassInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    /// <summary>The kind of type: "class", "record", or "annotation". Null for classes/interfaces.</summary>
    [JsonPropertyName("kind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; init; }

    [JsonPropertyName("entryPoint")]
    public bool? EntryPoint { get; init; }

    /// <summary>External package this type is re-exported from.</summary>
    [JsonPropertyName("reExportedFrom")]
    public string? ReExportedFrom { get; init; }

    [JsonPropertyName("extends")]
    public string? Extends { get; init; }

    [JsonPropertyName("implements")]
    public IReadOnlyList<string>? Implements { get; init; }

    [JsonPropertyName("doc")]
    public string? Doc { get; init; }

    [JsonPropertyName("modifiers")]
    public IReadOnlyList<string>? Modifiers { get; init; }

    [JsonPropertyName("typeParams")]
    public string? TypeParams { get; init; }

    [JsonPropertyName("constructors")]
    public IReadOnlyList<MethodInfo>? Constructors { get; init; }

    [JsonPropertyName("methods")]
    public IReadOnlyList<MethodInfo>? Methods { get; init; }

    [JsonPropertyName("fields")]
    public IReadOnlyList<FieldInfo>? Fields { get; init; }

    /// <summary>Returns true if this is a client class (SDK entry point with operations).
    /// A client type must be an entry point AND have methods.</summary>
    [JsonIgnore]
    public bool IsClientType =>
        EntryPoint == true &&
        (Methods?.Any() ?? false);

    /// <summary>Returns true if this is a model/DTO class.
    /// A model type has no public methods (and has fields or no methods at all),
    /// or all its public methods are getters/setters/is-checks.</summary>
    [JsonIgnore]
    public bool IsModelType
    {
        get
        {
            var hasPublicMethods = Methods?.Any(m => m.Modifiers?.Contains("public") == true) ?? false;

            if (!hasPublicMethods)
            {
                // No public methods: model if it has fields, or if Methods wasn't extracted (null).
                // An empty class (empty Methods list + no fields) is a marker type, not a model.
                return (Fields?.Count > 0) || Methods is null;
            }

            // All public methods are getters/setters/is-checks
            return Methods!.Where(m => m.Modifiers?.Contains("public") == true)
                .All(m => m.Name.StartsWith("get", StringComparison.Ordinal)
                    || m.Name.StartsWith("set", StringComparison.Ordinal)
                    || m.Name.StartsWith("is", StringComparison.Ordinal));
        }
    }

    /// <summary>Gets the priority for smart truncation. Lower = more important.</summary>
    [JsonIgnore]
    public int TruncationPriority
    {
        get
        {
            if (IsClientType) return 0;
            if (Name.EndsWith("Options", StringComparison.Ordinal) || Name.EndsWith("Config", StringComparison.Ordinal) || Name.EndsWith("Builder", StringComparison.Ordinal)) return 1;
            if (Name.Contains("Exception", StringComparison.Ordinal)) return 2;
            if (IsModelType) return 3;
            return 4;
        }
    }

    /// <summary>Gets type names referenced in method signatures.</summary>
    public HashSet<string> GetReferencedTypes(HashSet<string> allTypeNames)
    {
        HashSet<string> tokens = [];
        CollectReferencedTypes(allTypeNames, tokens);
        return tokens;
    }

    /// <summary>
    /// Populates <paramref name="result"/> with referenced type names.
    /// Clears the set first so callers can reuse it across iterations.
    /// </summary>
    public void CollectReferencedTypes(HashSet<string> allTypeNames, HashSet<string> result)
    {
        result.Clear();

        if (!string.IsNullOrEmpty(Extends))
        {
            var baseName = Extends.Split('<')[0];
            if (allTypeNames.Contains(baseName))
                result.Add(baseName);
        }

        foreach (var iface in Implements ?? [])
        {
            var ifaceName = iface.Split('<')[0];
            if (allTypeNames.Contains(ifaceName))
                result.Add(ifaceName);
        }

        foreach (var method in Methods ?? [])
        {
            SignatureTokenizer.TokenizeInto(method.Sig, result);
            SignatureTokenizer.TokenizeInto(method.Ret, result);
        }

        foreach (var field in Fields ?? [])
        {
            SignatureTokenizer.TokenizeInto(field.Type, result);
        }

        result.IntersectWith(allTypeNames);
    }
}

/// <summary>An enum type.</summary>
public sealed record EnumInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("doc")]
    public string? Doc { get; init; }

    [JsonPropertyName("values")]
    public IReadOnlyList<string>? Values { get; init; }

    [JsonPropertyName("methods")]
    public IReadOnlyList<MethodInfo>? Methods { get; init; }
}

/// <summary>A method or constructor.</summary>
public sealed record MethodInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("sig")]
    public string Sig { get; init; } = "";

    [JsonPropertyName("ret")]
    public string? Ret { get; init; }

    [JsonPropertyName("doc")]
    public string? Doc { get; init; }

    [JsonPropertyName("modifiers")]
    public IReadOnlyList<string>? Modifiers { get; init; }

    [JsonPropertyName("typeParams")]
    public string? TypeParams { get; init; }

    [JsonPropertyName("throws")]
    public IReadOnlyList<string>? Throws { get; init; }
}

/// <summary>A field or constant.</summary>
public sealed record FieldInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("doc")]
    public string? Doc { get; init; }

    [JsonPropertyName("modifiers")]
    public IReadOnlyList<string>? Modifiers { get; init; }

    [JsonPropertyName("value")]
    public string? Value { get; init; }
}

[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ApiIndex))]
internal sealed partial class SourceGenerationContext : JsonSerializerContext
{
    private static readonly Lazy<SourceGenerationContext> _indented = new(
        () => new SourceGenerationContext(
            new JsonSerializerOptions(Default!.Options!) { WriteIndented = true }));

    /// <summary>Context configured for indented (pretty) output.</summary>
    public static SourceGenerationContext Indented => _indented.Value;
}
