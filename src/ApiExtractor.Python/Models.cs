// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using ApiExtractor.Contracts;

namespace ApiExtractor.Python;

// Reuse the same output models for consistency across languages
public record ApiIndex(string Package, IReadOnlyList<ModuleInfo> Modules) : IApiIndex
{
    /// <summary>Gets all classes in the API.</summary>
    public IEnumerable<ClassInfo> GetAllClasses() =>
        Modules.SelectMany(m => m.Classes ?? []);
    
    /// <summary>Gets client classes (entry points for SDK operations).</summary>
    public IEnumerable<ClassInfo> GetClientClasses() =>
        GetAllClasses().Where(c => c.IsClientType);
    
    public string ToJson(bool pretty = false) => pretty
        ? JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })
        : JsonSerializer.Serialize(this);
    
    public string ToStubs() => PythonFormatter.Format(this);
}

public record ModuleInfo(string Name, IReadOnlyList<ClassInfo>? Classes, IReadOnlyList<FunctionInfo>? Functions);

public record ClassInfo(
    string Name,
    string? Base,
    string? Doc,
    IReadOnlyList<MethodInfo>? Methods,
    IReadOnlyList<PropertyInfo>? Properties)
{
    /// <summary>Returns true if this is a client class (SDK entry point).</summary>
    [JsonIgnore]
    public bool IsClientType =>
        (Name.EndsWith("Client") || Name.EndsWith("Service") || Name.EndsWith("Manager")) &&
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
            if (Name.Contains("Exception") || Name.Contains("Error")) return 2;
            if (IsModelType) return 3;
            return 4;
        }
    }
    
    /// <summary>Gets type names referenced in method signatures.</summary>
    public HashSet<string> GetReferencedTypes(HashSet<string> allTypeNames)
    {
        var refs = new HashSet<string>();
        
        if (!string.IsNullOrEmpty(Base))
        {
            var baseName = Base.Split('[')[0];
            if (allTypeNames.Contains(baseName))
                refs.Add(baseName);
        }
        
        foreach (var method in Methods ?? [])
        {
            foreach (var typeName in allTypeNames)
            {
                if (method.Signature.Contains(typeName))
                    refs.Add(typeName);
            }
        }
        
        foreach (var prop in Properties ?? [])
        {
            if (!string.IsNullOrEmpty(prop.Type))
            {
                foreach (var typeName in allTypeNames)
                {
                    if (prop.Type.Contains(typeName))
                        refs.Add(typeName);
                }
            }
        }
        
        return refs;
    }
}

public record MethodInfo(
    string Name,
    string Signature,
    string? Doc,
    bool? IsAsync,
    bool? IsClassMethod,
    bool? IsStaticMethod);

public record PropertyInfo(string Name, string? Type, string? Doc);

public record FunctionInfo(
    string Name,
    string Signature,
    string? Doc,
    bool? IsAsync);

[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ApiIndex))]
public partial class ApiIndexContext : JsonSerializerContext { }

public static class ApiIndexExtensions
{
    public static string ToJson(this ApiIndex index) =>
        JsonSerializer.Serialize(index, ApiIndexContext.Default.ApiIndex);
}
