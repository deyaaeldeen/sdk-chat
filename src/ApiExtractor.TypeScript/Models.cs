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

    [JsonPropertyName("modules")]
    public IReadOnlyList<ModuleInfo> Modules { get; init; } = [];

    /// <summary>Gets all classes in the API.</summary>
    public IEnumerable<ClassInfo> GetAllClasses() =>
        Modules.SelectMany(m => m.Classes ?? []);

    /// <summary>Gets client classes (entry points for SDK operations).</summary>
    public IEnumerable<ClassInfo> GetClientClasses() =>
        GetAllClasses().Where(c => c.IsClientType);

    public string ToJson(bool pretty = false) => pretty
        ? JsonSerializer.Serialize(this, SourceGenerationContext.Indented.ApiIndex)
        : JsonSerializer.Serialize(this, SourceGenerationContext.Default.ApiIndex);

    public string ToStubs() => TypeScriptFormatter.Format(this);
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

    /// <summary>Gets the priority for smart truncation. Lower = more important.</summary>
    [JsonIgnore]
    public int TruncationPriority
    {
        get
        {
            if (IsClientType) return 0;
            if (Name.EndsWith("Options") || Name.EndsWith("Config")) return 1;
            if (Name.Contains("Error")) return 2;
            if (IsModelType) return 3;
            return 4;
        }
    }

    /// <summary>Gets type names referenced in method signatures and properties.</summary>
    public HashSet<string> GetReferencedTypes(HashSet<string> allTypeNames)
    {
        var refs = new HashSet<string>();

        if (!string.IsNullOrEmpty(Extends))
        {
            var baseName = Extends.Split('<')[0];
            if (allTypeNames.Contains(baseName))
                refs.Add(baseName);
        }

        foreach (var iface in Implements ?? [])
        {
            var ifaceName = iface.Split('<')[0];
            if (allTypeNames.Contains(ifaceName))
                refs.Add(ifaceName);
        }

        foreach (var method in Methods ?? [])
        {
            foreach (var typeName in allTypeNames)
            {
                if (method.Sig.Contains(typeName) || (method.Ret?.Contains(typeName) ?? false))
                    refs.Add(typeName);
            }
        }

        foreach (var prop in Properties ?? [])
        {
            foreach (var typeName in allTypeNames)
            {
                if (prop.Type.Contains(typeName))
                    refs.Add(typeName);
            }
        }

        return refs;
    }
}

/// <summary>An interface declaration.</summary>
public record InterfaceInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("entryPoint")]
    public bool? EntryPoint { get; init; }

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
public record EnumInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("doc")]
    public string? Doc { get; init; }

    [JsonPropertyName("values")]
    public IReadOnlyList<string>? Values { get; init; }
}

/// <summary>A type alias.</summary>
public record TypeAliasInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("doc")]
    public string? Doc { get; init; }
}

/// <summary>A function declaration.</summary>
public record FunctionInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("entryPoint")]
    public bool? EntryPoint { get; init; }

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
public record MethodInfo
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
public record PropertyInfo
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
public record ConstructorInfo
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
    private static SourceGenerationContext? _indented;

    /// <summary>Context configured for indented (pretty) output.</summary>
    public static SourceGenerationContext Indented => _indented ??= new SourceGenerationContext(
        new JsonSerializerOptions(Default.Options) { WriteIndented = true });
}
