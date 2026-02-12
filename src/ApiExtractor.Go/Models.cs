// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using ApiExtractor.Contracts;

namespace ApiExtractor.Go;

/// <summary>Root container for extracted Go API.</summary>
public sealed record ApiIndex : IApiIndex
{
    [JsonPropertyName("package")]
    public string Package { get; init; } = "";

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("packages")]
    public IReadOnlyList<PackageApi> Packages { get; init; } = [];

    /// <summary>Types from external dependencies that are referenced in the public API.</summary>
    [JsonPropertyName("dependencies")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<DependencyInfo>? Dependencies { get; init; }

    /// <summary>Gets all structs in the API.</summary>
    public IEnumerable<StructApi> GetAllStructs() =>
        Packages.SelectMany(p => p.Structs ?? []);

    /// <summary>Gets client structs (entry points for SDK operations).</summary>
    public IEnumerable<StructApi> GetClientStructs() =>
        GetAllStructs().Where(s => s.IsClientType);

    public string ToJson(bool pretty = false) => pretty
        ? JsonSerializer.Serialize(this, SourceGenerationContext.Indented.ApiIndex)
        : JsonSerializer.Serialize(this, SourceGenerationContext.Default.ApiIndex);

    public string ToStubs() => GoFormatter.Format(this);

    /// <summary>
    /// Builds a dependency graph: for each struct, which other structs it references.
    /// Used for smart truncation to avoid orphan types.
    /// </summary>
    public Dictionary<string, HashSet<string>> BuildDependencyGraph()
    {
        var graph = new Dictionary<string, HashSet<string>>();
        var allStructs = GetAllStructs().ToList();
        var allTypeNames = allStructs.Select(s => s.Name).ToHashSet();
        HashSet<string> reusable = [];

        foreach (var st in allStructs)
        {
            st.CollectReferencedTypes(allTypeNames, reusable);
            graph[st.Name] = [.. reusable];
        }

        return graph;
    }
}

/// <summary>Information about types from a dependency module.</summary>
public sealed record DependencyInfo
{
    /// <summary>The module path (e.g., "github.com/Azure/azure-sdk-for-go/sdk/azcore").</summary>
    [JsonPropertyName("package")]
    public string Package { get; init; } = "";

    /// <summary>Structs from this module that are referenced in the API.</summary>
    [JsonPropertyName("structs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<StructApi>? Structs { get; init; }

    /// <summary>Interfaces from this module that are referenced in the API.</summary>
    [JsonPropertyName("interfaces")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<IfaceApi>? Interfaces { get; init; }

    /// <summary>Type aliases from this module that are referenced in the API.</summary>
    [JsonPropertyName("types")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<TypeApi>? Types { get; init; }
}

/// <summary>A Go package.</summary>
public sealed record PackageApi
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("doc")]
    public string? Doc { get; init; }

    [JsonPropertyName("structs")]
    public IReadOnlyList<StructApi>? Structs { get; init; }

    [JsonPropertyName("interfaces")]
    public IReadOnlyList<IfaceApi>? Interfaces { get; init; }

    [JsonPropertyName("functions")]
    public IReadOnlyList<FuncApi>? Functions { get; init; }

    [JsonPropertyName("types")]
    public IReadOnlyList<TypeApi>? Types { get; init; }

    [JsonPropertyName("constants")]
    public IReadOnlyList<ConstApi>? Constants { get; init; }

    [JsonPropertyName("variables")]
    public IReadOnlyList<VarApi>? Variables { get; init; }
}

/// <summary>A struct type.</summary>
public sealed record StructApi
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("entryPoint")]
    public bool? EntryPoint { get; init; }
    /// <summary>External module this type is re-exported from.</summary>
    [JsonPropertyName("reExportedFrom")]
    public string? ReExportedFrom { get; init; }
    [JsonPropertyName("doc")]
    public string? Doc { get; init; }

    /// <summary>Type parameters for generic structs (Go 1.18+).</summary>
    [JsonPropertyName("typeParams")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? TypeParams { get; init; }

    /// <summary>Embedded struct/interface names (Go composition pattern).</summary>
    [JsonPropertyName("embeds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Embeds { get; init; }

    [JsonPropertyName("fields")]
    public IReadOnlyList<FieldApi>? Fields { get; init; }

    [JsonPropertyName("methods")]
    public IReadOnlyList<FuncApi>? Methods { get; init; }

    /// <summary>Returns true if this is a client struct (SDK entry point with operations).
    /// A client type must be an entry point AND have methods.</summary>
    [JsonIgnore]
    public bool IsClientType =>
        EntryPoint == true &&
        (Methods?.Any() ?? false);

    /// <summary>Returns true if this is a model/DTO struct.</summary>
    [JsonIgnore]
    public bool IsModelType =>
        !(Methods?.Any() ?? false) && (Fields?.Any() ?? false);

    /// <summary>Returns true if this is an Options struct.</summary>
    [JsonIgnore]
    public bool IsOptionsType =>
        Name.EndsWith("Options", StringComparison.Ordinal) || Name.EndsWith("Config", StringComparison.Ordinal) || Name.EndsWith("Params", StringComparison.Ordinal);

    /// <summary>Gets the priority for smart truncation. Lower = more important.</summary>
    [JsonIgnore]
    public int TruncationPriority
    {
        get
        {
            if (IsClientType) return 0;
            if (IsOptionsType) return 1;
            if (Name.Contains("Error", StringComparison.Ordinal) || Name.Contains("Exception", StringComparison.Ordinal)) return 2;
            if (IsModelType) return 3;
            return 4;
        }
    }

    /// <summary>Gets type names referenced in method signatures, fields, and embeds.</summary>
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

        foreach (var embed in Embeds ?? [])
        {
            var embedName = embed.Split('<')[0].TrimStart('*');
            if (allTypeNames.Contains(embedName))
                result.Add(embedName);
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

/// <summary>An interface type.</summary>
public sealed record IfaceApi
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("entryPoint")]
    public bool? EntryPoint { get; init; }
    /// <summary>External module this type is re-exported from.</summary>
    [JsonPropertyName("reExportedFrom")]
    public string? ReExportedFrom { get; init; }
    [JsonPropertyName("doc")]
    public string? Doc { get; init; }

    /// <summary>Embedded interface names (Go interface composition).</summary>
    [JsonPropertyName("embeds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Embeds { get; init; }

    [JsonPropertyName("methods")]
    public IReadOnlyList<FuncApi>? Methods { get; init; }
}

/// <summary>A function or method.</summary>
public sealed record FuncApi
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("entryPoint")]
    public bool? EntryPoint { get; init; }

    /// <summary>External module this function is re-exported from.</summary>
    [JsonPropertyName("reExportedFrom")]
    public string? ReExportedFrom { get; init; }

    /// <summary>Type parameters for generic functions (Go 1.18+).</summary>
    [JsonPropertyName("typeParams")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? TypeParams { get; init; }

    [JsonPropertyName("sig")]
    public string Sig { get; init; } = "";

    [JsonPropertyName("ret")]
    public string? Ret { get; init; }

    [JsonPropertyName("doc")]
    public string? Doc { get; init; }

    [JsonPropertyName("method")]
    public bool? IsMethod { get; init; }

    [JsonPropertyName("recv")]
    public string? Receiver { get; init; }
}

/// <summary>A struct field.</summary>
public sealed record FieldApi
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("tag")]
    public string? Tag { get; init; }

    [JsonPropertyName("doc")]
    public string? Doc { get; init; }
}

/// <summary>A type alias.</summary>
public sealed record TypeApi
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    /// <summary>External module this type alias references.</summary>
    [JsonPropertyName("reExportedFrom")]
    public string? ReExportedFrom { get; init; }

    [JsonPropertyName("doc")]
    public string? Doc { get; init; }
}

/// <summary>A constant.</summary>
public sealed record ConstApi
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("value")]
    public string? Value { get; init; }

    [JsonPropertyName("doc")]
    public string? Doc { get; init; }
}

/// <summary>A variable.</summary>
public sealed record VarApi
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("doc")]
    public string? Doc { get; init; }
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
