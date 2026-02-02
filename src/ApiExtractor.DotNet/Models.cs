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
            var deps = new HashSet<string>();

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

            // Check member signatures for type references
            foreach (var member in type.Members ?? [])
            {
                foreach (var typeName in allTypeNames)
                {
                    if (member.Signature.Contains(typeName))
                        deps.Add(typeName);
                }
            }

            graph[type.Name] = deps;
        }

        return graph;
    }
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
    /// Returns true if this is a client class (entry point for SDK operations).
    /// </summary>
    [JsonIgnore]
    public bool IsClientType =>
        Kind == "class" &&
        (Name.EndsWith("Client") || Name.EndsWith("Service") || Name.EndsWith("Manager")) &&
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
    /// Returns true if this is an Options type for configuration.
    /// </summary>
    [JsonIgnore]
    public bool IsOptionsType =>
        Name.EndsWith("Options") || Name.EndsWith("Settings") || Name.EndsWith("Config");

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
            if (IsOptionsType) return 1;       // Options next (configure clients)
            if (Name.Contains("Exception")) return 2; // Exceptions for error handling
            if (Kind == "enum") return 3;      // Enums usually small, include them
            if (IsModelType) return 4;         // Models are common but secondary
            return 5;                          // Everything else
        }
    }

    /// <summary>
    /// Gets all type names referenced by this type's members.
    /// </summary>
    public HashSet<string> GetReferencedTypes(HashSet<string> allTypeNames)
    {
        var refs = new HashSet<string>();

        if (!string.IsNullOrEmpty(Base))
        {
            var baseName = Base.Split('<')[0];
            if (allTypeNames.Contains(baseName))
                refs.Add(baseName);
        }

        foreach (var iface in Interfaces ?? [])
        {
            var ifaceName = iface.Split('<')[0];
            if (allTypeNames.Contains(ifaceName))
                refs.Add(ifaceName);
        }

        foreach (var member in Members ?? [])
        {
            foreach (var typeName in allTypeNames)
            {
                if (member.Signature.Contains(typeName))
                    refs.Add(typeName);
            }
        }

        return refs;
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
internal partial class JsonContext : JsonSerializerContext
{
    private static JsonContext? _indented;

    /// <summary>Context configured for indented (pretty) output.</summary>
    public static JsonContext Indented => _indented ??= new JsonContext(
        new JsonSerializerOptions(Default.Options) { WriteIndented = true });
}
