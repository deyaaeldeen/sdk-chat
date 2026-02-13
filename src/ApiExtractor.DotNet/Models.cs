// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using ApiExtractor.Contracts;

namespace ApiExtractor.DotNet;

/// <summary>
/// Output model for extracted API surface.
/// Minimal schema - only what AI needs to understand the SDK.
/// </summary>
public sealed record ApiIndex : IApiIndex
{
    [JsonPropertyName("package")]
    public string Package { get; init; } = "";

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("namespaces")]
    public IReadOnlyList<NamespaceInfo> Namespaces { get; init; } = [];

    /// <summary>Types from external dependencies that are referenced in the public API.</summary>
    [JsonPropertyName("dependencies")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<DependencyInfo>? Dependencies { get; init; }

    public string ToJson(bool pretty = false) => pretty
        ? JsonSerializer.Serialize(this, JsonContext.Indented.ApiIndex)
        : JsonSerializer.Serialize(this, JsonContext.Default.ApiIndex);

    public string ToStubs() => CSharpFormatter.Format(this);

    /// <summary>
    /// Gets a flattened list of all types in the API.
    /// </summary>
    public IEnumerable<TypeInfo> GetAllTypes() =>
        Namespaces.SelectMany(ns => ns.Types);

    /// <summary>
    /// Gets client types (classes ending with "Client" that have operations).
    /// </summary>
    public IEnumerable<TypeInfo> GetClientTypes() =>
        GetAllTypes().Where(t => t.IsClientType);

    /// <summary>Gets the names of all types in the API surface.</summary>
    public IEnumerable<string> GetAllTypeNames() =>
        GetAllTypes().Select(t => t.Name);

    /// <summary>Gets the names of client/entry-point types.</summary>
    public IEnumerable<string> GetClientTypeNames() =>
        GetClientTypes().Select(t => t.Name);

    /// <summary>
    /// Builds a dependency graph: for each type, which other types it references.
    /// Used for smart truncation to avoid orphan types.
    /// </summary>
    public Dictionary<string, HashSet<string>> BuildDependencyGraph()
    {
        var graph = new Dictionary<string, HashSet<string>>();
        var allTypeNames = GetAllTypes().Select(t => t.Name.Split('<')[0]).ToHashSet();

        foreach (var type in GetAllTypes())
        {
            HashSet<string> deps = [];

            // Check base type
            if (!string.IsNullOrEmpty(type.Base) && allTypeNames.Contains(type.Base.Split('<')[0]))
                deps.Add(type.Base.Split('<')[0]);

            // Check interfaces
            foreach (var iface in type.Interfaces ?? [])
            {
                var ifaceName = iface.Split('<')[0];
                if (allTypeNames.Contains(ifaceName))
                    deps.Add(ifaceName);
            }

            // Check member signatures for type references using token-boundary matching
            // (avoids substring false positives like "Policy" matching inside "PolicyList")
            foreach (var member in type.Members ?? [])
            {
                SignatureTokenizer.TokenizeInto(member.Signature, deps);
            }

            // Keep only references to known types
            deps.IntersectWith(allTypeNames);

            graph[type.Name] = deps;
        }

        return graph;
    }
}

/// <summary>Information about types from a dependency package/assembly.</summary>
public sealed record DependencyInfo
{
    /// <summary>The package name (e.g., "Azure.Core").</summary>
    [JsonPropertyName("package")]
    public string Package { get; init; } = "";

    /// <summary>Types from this package that are referenced in the API.</summary>
    [JsonPropertyName("types")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<TypeInfo>? Types { get; init; }
}

public record NamespaceInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("types")]
    public IReadOnlyList<TypeInfo> Types { get; init; } = [];
}

public record TypeInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = ""; // class, interface, struct, enum, record, delegate

    [JsonPropertyName("entryPoint")]
    public bool? EntryPoint { get; init; }

    [JsonPropertyName("isError")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsError { get; init; }

    /// <summary>External package/assembly this type is re-exported from.</summary>
    [JsonPropertyName("reExportedFrom")]
    public string? ReExportedFrom { get; init; }

    [JsonPropertyName("base")]
    public string? Base { get; init; }

    [JsonPropertyName("interfaces")]
    public IReadOnlyList<string>? Interfaces { get; init; }

    [JsonPropertyName("doc")]
    public string? Doc { get; init; }

    [JsonPropertyName("members")]
    public IReadOnlyList<MemberInfo>? Members { get; init; }

    [JsonPropertyName("values")]
    public IReadOnlyList<string>? Values { get; init; } // For enums

    /// <summary>
    /// Returns true if this is a client type (entry point for SDK operations).
    /// A client type must be an entry point AND have methods (operations).
    /// Allows class, struct, and record kinds — not just class.
    /// </summary>
    [JsonIgnore]
    public bool IsClientType =>
        EntryPoint == true &&
        (Members?.Any(m => m.Kind == "method") ?? false);

    /// <summary>
    /// Returns true if this is a model/DTO type (no methods, just properties).
    /// </summary>
    [JsonIgnore]
    public bool IsModelType =>
        (Kind == "class" || Kind == "record" || Kind == "struct") &&
        !(Members?.Any(m => m.Kind == "method") ?? false) &&
        (Members?.Any(m => m.Kind == "property") ?? false);

    /// <summary>
    /// Returns true if this type inherits from System.Exception.
    /// Set structurally by the Roslyn-based extractor via inheritance chain analysis.
    /// </summary>
    [JsonIgnore]
    public bool IsErrorType => IsError == true;

    /// <summary>
    /// Gets the priority for smart truncation.
    /// Lower = more important, include first.
    /// </summary>
    [JsonIgnore]
    public int TruncationPriority
    {
        get
        {
            if (IsClientType) return 0;        // Clients are most important
            if (IsErrorType) return 1;         // Error types for error handling
            if (Kind == "enum") return 2;      // Enums usually small, include them
            if (IsModelType) return 3;         // Models are common but secondary
            return 4;                          // Everything else
        }
    }

    /// <summary>
    /// Gets all type names referenced by this type's members.
    /// </summary>
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
            var baseName = Base.Split('<')[0];
            if (allTypeNames.Contains(baseName))
                result.Add(baseName);
        }

        foreach (var iface in Interfaces ?? [])
        {
            var ifaceName = iface.Split('<')[0];
            if (allTypeNames.Contains(ifaceName))
                result.Add(ifaceName);
        }

        foreach (var member in Members ?? [])
        {
            SignatureTokenizer.TokenizeInto(member.Signature, result);
        }

        result.IntersectWith(allTypeNames);
    }
}

public record MemberInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = ""; // ctor, method, property, field, event, indexer

    [JsonPropertyName("sig")]
    public string Signature { get; init; } = ""; // Compressed signature

    [JsonPropertyName("doc")]
    public string? Doc { get; init; }

    [JsonPropertyName("static")]
    public bool? IsStatic { get; init; }

    [JsonPropertyName("async")]
    public bool? IsAsync { get; init; }
}

[JsonSerializable(typeof(ApiIndex))]
[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class JsonContext : JsonSerializerContext
{
    private static readonly Lazy<JsonContext> _indented = new(
        () => new JsonContext(
            new JsonSerializerOptions(Default!.Options!) { WriteIndented = true }));

    /// <summary>Context configured for indented (pretty) output.</summary>
    public static JsonContext Indented => _indented.Value;
}
