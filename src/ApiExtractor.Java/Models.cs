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

    /// <summary>Gets all classes in the API.</summary>
    public IEnumerable<ClassInfo> GetAllClasses() =>
        Packages.SelectMany(p => p.Classes ?? []);

    /// <summary>Gets client classes (entry points for SDK operations).</summary>
    public IEnumerable<ClassInfo> GetClientClasses() =>
        GetAllClasses().Where(c => c.IsClientType);

    public string ToJson(bool pretty = false) => pretty
        ? JsonSerializer.Serialize(this, JsonOptionsCache.Indented)
        : JsonSerializer.Serialize(this);

    public string ToStubs() => JavaFormatter.Format(this);
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
}

/// <summary>A class or interface.</summary>
public sealed record ClassInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

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

    /// <summary>Returns true if this is a client class (SDK entry point).</summary>
    [JsonIgnore]
    public bool IsClientType =>
        (Name.EndsWith("Client") || Name.EndsWith("Service") || Name.EndsWith("Manager") || Name.EndsWith("Builder")) &&
        (Methods?.Any() ?? false);

    /// <summary>Returns true if this is a model/DTO class.</summary>
    [JsonIgnore]
    public bool IsModelType =>
        !(Methods?.Any(m => m.Modifiers?.Contains("public") == true) ?? false) ||
        (Methods?.All(m => m.Name.StartsWith("get") || m.Name.StartsWith("set") || m.Name.StartsWith("is")) ?? false);

    /// <summary>Gets the priority for smart truncation. Lower = more important.</summary>
    [JsonIgnore]
    public int TruncationPriority
    {
        get
        {
            if (IsClientType) return 0;
            if (Name.EndsWith("Options") || Name.EndsWith("Config") || Name.EndsWith("Builder")) return 1;
            if (Name.Contains("Exception")) return 2;
            if (IsModelType) return 3;
            return 4;
        }
    }

    /// <summary>Gets type names referenced in method signatures.</summary>
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

/// <summary>An enum type.</summary>
public record EnumInfo
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

    [JsonPropertyName("modifiers")]
    public IReadOnlyList<string>? Modifiers { get; init; }

    [JsonPropertyName("typeParams")]
    public string? TypeParams { get; init; }

    [JsonPropertyName("throws")]
    public IReadOnlyList<string>? Throws { get; init; }
}

/// <summary>A field or constant.</summary>
public record FieldInfo
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
internal partial class SourceGenerationContext : JsonSerializerContext { }
