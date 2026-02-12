// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using ApiExtractor.Contracts;

namespace ApiExtractor.Python;

// Reuse the same output models for consistency across languages
public sealed record ApiIndex(
    string Package,
    IReadOnlyList<ModuleInfo> Modules,
    IReadOnlyList<DependencyInfo>? Dependencies = null,
    string? Version = null) : IApiIndex
{
    /// <summary>Gets all classes in the API.</summary>
    public IEnumerable<ClassInfo> GetAllClasses() =>
        Modules.SelectMany(m => m.Classes ?? []);

    /// <summary>Gets client classes (entry points for SDK operations).</summary>
    public IEnumerable<ClassInfo> GetClientClasses() =>
        GetAllClasses().Where(c => c.IsClientType);

    public string ToJson(bool pretty = false) => pretty
        ? JsonSerializer.Serialize(this, ApiIndexContext.Indented.ApiIndex)
        : JsonSerializer.Serialize(this, ApiIndexContext.Default.ApiIndex);

    public string ToStubs() => PythonFormatter.Format(this);

    /// <summary>
    /// Builds a dependency graph: for each class, which other classes it references.
    /// Used for smart truncation to avoid orphan types.
    /// </summary>
    public Dictionary<string, HashSet<string>> BuildDependencyGraph()
    {
        var graph = new Dictionary<string, HashSet<string>>();
        var allTypeNames = GetAllClasses().Select(c => c.Name).ToHashSet();
        HashSet<string> reusable = [];

        foreach (var cls in GetAllClasses())
        {
            cls.CollectReferencedTypes(allTypeNames, reusable);
            graph[cls.Name] = [.. reusable];
        }

        return graph;
    }
}

/// <summary>Information about types from a dependency package.</summary>
public sealed record DependencyInfo
{
    /// <summary>The package name (e.g., "azure-core").</summary>
    [JsonPropertyName("package")]
    public string Package { get; init; } = "";

    /// <summary>Classes from this package that are referenced in the API.</summary>
    [JsonPropertyName("classes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ClassInfo>? Classes { get; init; }

    /// <summary>Functions from this package that are referenced in the API.</summary>
    [JsonPropertyName("functions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<FunctionInfo>? Functions { get; init; }
}

public sealed record ModuleInfo(string Name, IReadOnlyList<ClassInfo>? Classes, IReadOnlyList<FunctionInfo>? Functions);

public sealed record ClassInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("entryPoint")]
    public bool? EntryPoint { get; init; }
    /// <summary>External package this type is re-exported from.</summary>
    [JsonPropertyName("reExportedFrom")]
    public string? ReExportedFrom { get; init; }
    [JsonPropertyName("base")]
    public string? Base { get; init; }

    [JsonPropertyName("doc")]
    public string? Doc { get; init; }

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

    /// <summary>Gets the priority for smart truncation. Lower = more important.</summary>
    [JsonIgnore]
    public int TruncationPriority
    {
        get
        {
            if (IsClientType) return 0;
            if (Name.EndsWith("Options", StringComparison.Ordinal) || Name.EndsWith("Config", StringComparison.Ordinal)) return 1;
            if (Name.Contains("Exception", StringComparison.Ordinal) || Name.Contains("Error", StringComparison.Ordinal)) return 2;
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

        if (!string.IsNullOrEmpty(Base))
        {
            var baseName = Base.Split('[')[0];
            if (allTypeNames.Contains(baseName))
                result.Add(baseName);
        }

        foreach (var method in Methods ?? [])
        {
            SignatureTokenizer.TokenizeInto(method.Signature, result);
            SignatureTokenizer.TokenizeInto(method.Ret, result);
        }

        foreach (var prop in Properties ?? [])
        {
            SignatureTokenizer.TokenizeInto(prop.Type, result);
        }

        result.IntersectWith(allTypeNames);
    }
}

public record MethodInfo(
    string Name,
    string Signature,
    string? Doc,
    bool? IsAsync,
    bool? IsClassMethod,
    bool? IsStaticMethod,
    [property: JsonPropertyName("ret")] string? Ret = null);

public record PropertyInfo(string Name, string? Type, string? Doc);

public sealed record FunctionInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("entryPoint")]
    public bool? EntryPoint { get; init; }

    /// <summary>External package this function is re-exported from.</summary>
    [JsonPropertyName("reExportedFrom")]
    public string? ReExportedFrom { get; init; }

    [JsonPropertyName("sig")]
    public string Signature { get; init; } = "";

    [JsonPropertyName("ret")]
    public string? Ret { get; init; }

    [JsonPropertyName("doc")]
    public string? Doc { get; init; }

    [JsonPropertyName("async")]
    public bool? IsAsync { get; init; }
}

[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ApiIndex))]
public partial class ApiIndexContext : JsonSerializerContext
{
    private static readonly Lazy<ApiIndexContext> _indented = new(
        () => new ApiIndexContext(
            new JsonSerializerOptions(Default!.Options) { WriteIndented = true }));

    /// <summary>Context configured for indented (pretty) output.</summary>
    public static ApiIndexContext Indented => _indented.Value;
}

public static class ApiIndexExtensions
{
    public static string ToJson(this ApiIndex index) =>
        JsonSerializer.Serialize(index, ApiIndexContext.Default.ApiIndex);
}
