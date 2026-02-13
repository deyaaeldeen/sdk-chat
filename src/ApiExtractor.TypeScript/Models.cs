// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using ApiExtractor.Contracts;

namespace ApiExtractor.TypeScript;

/// <summary>Root container for extracted TypeScript API.</summary>
public sealed record ApiIndex : IApiIndex
{
    [JsonPropertyName("package")]
    public string Package { get; init; } = "";

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("modules")]
    public IReadOnlyList<ModuleInfo> Modules { get; init; } = [];

    /// <summary>Types from dependency packages that appear in the public API.</summary>
    [JsonPropertyName("dependencies")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<DependencyInfo>? Dependencies { get; init; }

    /// <summary>Gets all classes in the API.</summary>
    public IEnumerable<ClassInfo> GetAllClasses() =>
        Modules.SelectMany(m => m.Classes ?? []);

    /// <summary>Gets client classes (entry points for SDK operations).</summary>
    public IEnumerable<ClassInfo> GetClientClasses() =>
        GetAllClasses().Where(c => c.IsClientType);

    /// <summary>Gets the names of all types (classes, interfaces, enums, type aliases) in the API surface.</summary>
    public IEnumerable<string> GetAllTypeNames() =>
        Modules.SelectMany(m =>
            (m.Classes ?? []).Select(c => c.Name)
                .Concat((m.Interfaces ?? []).Select(i => i.Name))
                .Concat((m.Enums ?? []).Select(e => e.Name))
                .Concat((m.Types ?? []).Select(t => t.Name)));

    /// <summary>Gets the names of client/entry-point types.</summary>
    public IEnumerable<string> GetClientTypeNames() =>
        GetClientClasses().Select(c => c.Name);

    public string ToJson(bool pretty = false) => pretty
        ? JsonSerializer.Serialize(this, SourceGenerationContext.Indented.ApiIndex)
        : JsonSerializer.Serialize(this, SourceGenerationContext.Default.ApiIndex);

    public string ToStubs() => TypeScriptFormatter.Format(this);

    /// <summary>
    /// Builds a dependency graph: for each class, which other classes it references.
    /// Used for smart truncation to avoid orphan types.
    /// </summary>
    public Dictionary<string, HashSet<string>> BuildDependencyGraph()
    {
        var graph = new Dictionary<string, HashSet<string>>();
        var allClasses = GetAllClasses().ToList();
        var allTypeNames = allClasses.Select(c => c.Name).ToHashSet();

        // Include interfaces, enums, and type aliases in the type name set
        foreach (var m in Modules)
        {
            foreach (var i in m.Interfaces ?? []) allTypeNames.Add(i.Name);
            foreach (var e in m.Enums ?? []) allTypeNames.Add(e.Name);
            foreach (var t in m.Types ?? []) allTypeNames.Add(t.Name);
        }

        HashSet<string> reusable = [];

        foreach (var cls in allClasses)
        {
            cls.CollectReferencedTypes(allTypeNames, reusable);
            graph[cls.Name] = new HashSet<string>(reusable);
        }

        return graph;
    }
}

/// <summary>Information about types from a dependency package.</summary>
public sealed record DependencyInfo
{
    /// <summary>The npm package name.</summary>
    [JsonPropertyName("package")]
    public string Package { get; init; } = "";

    /// <summary>Classes from this package that are referenced in the API.</summary>
    [JsonPropertyName("classes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ClassInfo>? Classes { get; init; }

    /// <summary>Interfaces from this package that are referenced in the API.</summary>
    [JsonPropertyName("interfaces")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<InterfaceInfo>? Interfaces { get; init; }

    /// <summary>Enums from this package that are referenced in the API.</summary>
    [JsonPropertyName("enums")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<EnumInfo>? Enums { get; init; }

    /// <summary>Type aliases from this package that are referenced in the API.</summary>
    [JsonPropertyName("types")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<TypeAliasInfo>? Types { get; init; }
}

/// <summary>A TypeScript module/file.</summary>
public sealed record ModuleInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("classes")]
    public IReadOnlyList<ClassInfo>? Classes { get; init; }

    [JsonPropertyName("interfaces")]
    public IReadOnlyList<InterfaceInfo>? Interfaces { get; init; }

    [JsonPropertyName("enums")]
    public IReadOnlyList<EnumInfo>? Enums { get; init; }

    [JsonPropertyName("types")]
    public IReadOnlyList<TypeAliasInfo>? Types { get; init; }

    [JsonPropertyName("functions")]
    public IReadOnlyList<FunctionInfo>? Functions { get; init; }
}

/// <summary>A class declaration.</summary>
public sealed record ClassInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("entryPoint")]
    public bool? EntryPoint { get; init; }

    /// <summary>The subpath to import from (e.g., "." or "./client").</summary>
    [JsonPropertyName("exportPath")]
    public string? ExportPath { get; init; }

    /// <summary>External package this type is re-exported from (e.g., "@azure/core-client").</summary>
    [JsonPropertyName("reExportedFrom")]
    public string? ReExportedFrom { get; init; }

    [JsonPropertyName("extends")]
    public string? Extends { get; init; }

    [JsonPropertyName("implements")]
    public IReadOnlyList<string>? Implements { get; init; }

    [JsonPropertyName("typeParams")]
    public string? TypeParams { get; init; }

    [JsonPropertyName("doc")]
    public string? Doc { get; init; }

    [JsonPropertyName("constructors")]
    public IReadOnlyList<ConstructorInfo>? Constructors { get; init; }

    [JsonPropertyName("methods")]
    public IReadOnlyList<MethodInfo>? Methods { get; init; }

    [JsonPropertyName("properties")]
    public IReadOnlyList<PropertyInfo>? Properties { get; init; }

    /// <summary>Returns true if this is a client class (SDK entry point with operations).
    /// A client type must be an entry point AND have methods.</summary>
    [JsonIgnore]
    public bool IsClientType =>
        EntryPoint == true &&
        (Methods?.Any() ?? false);

    /// <summary>Returns true if this is a model/DTO class.</summary>
    [JsonIgnore]
    public bool IsModelType =>
        !(Methods?.Any() ?? false) && (Properties?.Any() ?? false);

    /// <summary>Returns true if this class extends an error base type.
    /// Checks the Extends field structurally rather than the type's own name.</summary>
    [JsonIgnore]
    public bool IsErrorType
    {
        get
        {
            if (string.IsNullOrEmpty(Extends)) return false;
            var baseName = Extends.Split('<')[0];
            return baseName.EndsWith("Error", StringComparison.Ordinal)
                || baseName.EndsWith("Exception", StringComparison.Ordinal);
        }
    }

    /// <summary>Gets the priority for smart truncation. Lower = more important.</summary>
    [JsonIgnore]
    public int TruncationPriority
    {
        get
        {
            if (IsClientType) return 0;
            if (IsErrorType) return 1;
            if (IsModelType) return 2;
            return 3;
        }
    }

    /// <summary>Gets type names referenced in method signatures and properties.</summary>
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

        foreach (var prop in Properties ?? [])
        {
            SignatureTokenizer.TokenizeInto(prop.Type, result);
        }

        result.IntersectWith(allTypeNames);
    }
}

/// <summary>An interface declaration.</summary>
public sealed record InterfaceInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("entryPoint")]
    public bool? EntryPoint { get; init; }

    /// <summary>The subpath to import from (e.g., "." or "./client").</summary>
    [JsonPropertyName("exportPath")]
    public string? ExportPath { get; init; }

    /// <summary>External package this type is re-exported from.</summary>
    [JsonPropertyName("reExportedFrom")]
    public string? ReExportedFrom { get; init; }

    [JsonPropertyName("extends")]
    public string? Extends { get; init; }

    [JsonPropertyName("typeParams")]
    public string? TypeParams { get; init; }

    [JsonPropertyName("doc")]
    public string? Doc { get; init; }

    [JsonPropertyName("methods")]
    public IReadOnlyList<MethodInfo>? Methods { get; init; }

    [JsonPropertyName("properties")]
    public IReadOnlyList<PropertyInfo>? Properties { get; init; }
}

/// <summary>An enum declaration.</summary>
public sealed record EnumInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";
    /// <summary>External package this type is re-exported from.</summary>
    [JsonPropertyName("reExportedFrom")]
    public string? ReExportedFrom { get; init; }
    [JsonPropertyName("doc")]
    public string? Doc { get; init; }

    [JsonPropertyName("values")]
    public IReadOnlyList<string>? Values { get; init; }
}

/// <summary>A type alias.</summary>
public sealed record TypeAliasInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    /// <summary>External package this type is re-exported from.</summary>
    [JsonPropertyName("reExportedFrom")]
    public string? ReExportedFrom { get; init; }

    [JsonPropertyName("doc")]
    public string? Doc { get; init; }

    /// <summary>Gets type names referenced in this type alias definition.</summary>
    public void CollectReferencedTypes(HashSet<string> allTypeNames, HashSet<string> result)
    {
        result.Clear();
        SignatureTokenizer.TokenizeInto(Type, result);
        result.IntersectWith(allTypeNames);
    }
}

/// <summary>A function declaration.</summary>
public sealed record FunctionInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("entryPoint")]
    public bool? EntryPoint { get; init; }

    /// <summary>The subpath to import from (e.g., "." or "./client").</summary>
    [JsonPropertyName("exportPath")]
    public string? ExportPath { get; init; }

    /// <summary>External package this function is re-exported from.</summary>
    [JsonPropertyName("reExportedFrom")]
    public string? ReExportedFrom { get; init; }

    [JsonPropertyName("sig")]
    public string Sig { get; init; } = "";

    [JsonPropertyName("ret")]
    public string? Ret { get; init; }

    [JsonPropertyName("doc")]
    public string? Doc { get; init; }

    [JsonPropertyName("async")]
    public bool? Async { get; init; }
}

/// <summary>A method declaration.</summary>
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

    [JsonPropertyName("async")]
    public bool? Async { get; init; }

    [JsonPropertyName("static")]
    public bool? Static { get; init; }
}

/// <summary>A property declaration.</summary>
public sealed record PropertyInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("doc")]
    public string? Doc { get; init; }

    [JsonPropertyName("readonly")]
    public bool? Readonly { get; init; }

    [JsonPropertyName("optional")]
    public bool? Optional { get; init; }
}

/// <summary>A constructor declaration.</summary>
public sealed record ConstructorInfo
{
    [JsonPropertyName("sig")]
    public string Sig { get; init; } = "";
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
