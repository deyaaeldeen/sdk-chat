// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using ApiExtractor.Contracts;

namespace ApiExtractor.Go;

/// <summary>Root container for extracted Go API.</summary>
public record ApiIndex : IApiIndex
{
    [JsonPropertyName("package")]
    public string Package { get; init; } = "";

    [JsonPropertyName("packages")]
    public IReadOnlyList<PackageApi> Packages { get; init; } = [];
    
    /// <summary>Gets all structs in the API.</summary>
    public IEnumerable<StructApi> GetAllStructs() =>
        Packages.SelectMany(p => p.Structs ?? []);
    
    /// <summary>Gets client structs (entry points for SDK operations).</summary>
    public IEnumerable<StructApi> GetClientStructs() =>
        GetAllStructs().Where(s => s.IsClientType);
    
    public string ToJson(bool pretty = false) => pretty
        ? JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })
        : JsonSerializer.Serialize(this);
    
    public string ToStubs() => GoFormatter.Format(this);
}

/// <summary>A Go package.</summary>
public record PackageApi
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
public record StructApi
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("doc")]
    public string? Doc { get; init; }

    [JsonPropertyName("fields")]
    public IReadOnlyList<FieldApi>? Fields { get; init; }

    [JsonPropertyName("methods")]
    public IReadOnlyList<FuncApi>? Methods { get; init; }
    
    /// <summary>Returns true if this is a client struct (SDK entry point).</summary>
    [JsonIgnore]
    public bool IsClientType =>
        (Name.EndsWith("Client") || Name.EndsWith("Service") || Name.EndsWith("Manager")) &&
        (Methods?.Any() ?? false);
    
    /// <summary>Returns true if this is a model/DTO struct.</summary>
    [JsonIgnore]
    public bool IsModelType =>
        !(Methods?.Any() ?? false) && (Fields?.Any() ?? false);
    
    /// <summary>Returns true if this is an Options struct.</summary>
    [JsonIgnore]
    public bool IsOptionsType =>
        Name.EndsWith("Options") || Name.EndsWith("Config") || Name.EndsWith("Params");
    
    /// <summary>Gets the priority for smart truncation. Lower = more important.</summary>
    [JsonIgnore]
    public int TruncationPriority
    {
        get
        {
            if (IsClientType) return 0;
            if (IsOptionsType) return 1;
            if (Name.Contains("Error")) return 2;
            if (IsModelType) return 3;
            return 4;
        }
    }
    
    /// <summary>Gets type names referenced in method signatures and fields.</summary>
    public HashSet<string> GetReferencedTypes(HashSet<string> allTypeNames)
    {
        var refs = new HashSet<string>();
        
        foreach (var method in Methods ?? [])
        {
            foreach (var typeName in allTypeNames)
            {
                if (method.Sig.Contains(typeName) || (method.Ret?.Contains(typeName) ?? false))
                    refs.Add(typeName);
            }
        }
        
        foreach (var field in Fields ?? [])
        {
            foreach (var typeName in allTypeNames)
            {
                if (field.Type.Contains(typeName))
                    refs.Add(typeName);
            }
        }
        
        return refs;
    }
}

/// <summary>An interface type.</summary>
public record IfaceApi
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("doc")]
    public string? Doc { get; init; }

    [JsonPropertyName("methods")]
    public IReadOnlyList<FuncApi>? Methods { get; init; }
}

/// <summary>A function or method.</summary>
public record FuncApi
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

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
public record FieldApi
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
public record TypeApi
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("doc")]
    public string? Doc { get; init; }
}

/// <summary>A constant.</summary>
public record ConstApi
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
public record VarApi
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
internal partial class SourceGenerationContext : JsonSerializerContext { }
