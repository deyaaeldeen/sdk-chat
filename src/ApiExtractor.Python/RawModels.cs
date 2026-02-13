// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace ApiExtractor.Python;

/// <summary>
/// Source-generated JSON context for deserializing raw Python extractor output.
/// These DTOs match the JSON schema emitted by extract_api.py and are converted
/// to the domain <see cref="ApiIndex"/> model by <see cref="PythonApiExtractor"/>.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(RawPythonApiIndex))]
[JsonSerializable(typeof(RawPythonModule))]
[JsonSerializable(typeof(RawPythonClass))]
[JsonSerializable(typeof(RawPythonMethod))]
[JsonSerializable(typeof(RawPythonProperty))]
[JsonSerializable(typeof(RawPythonFunction))]
[JsonSerializable(typeof(RawPythonDependency))]
[JsonSerializable(typeof(List<RawPythonDependency>))]
public sealed partial class RawPythonJsonContext : JsonSerializerContext;

#region Raw Python API Models

public sealed record RawPythonApiIndex(
    [property: JsonPropertyName("package")] string? Package,
    [property: JsonPropertyName("modules")] List<RawPythonModule>? Modules,
    [property: JsonPropertyName("dependencies")] List<RawPythonDependency>? Dependencies = null
);

public sealed record RawPythonModule(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("classes")] List<RawPythonClass>? Classes,
    [property: JsonPropertyName("functions")] List<RawPythonFunction>? Functions
);

public sealed record RawPythonClass(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("base")] string? Base,
    [property: JsonPropertyName("doc")] string? Doc,
    [property: JsonPropertyName("methods")] List<RawPythonMethod>? Methods,
    [property: JsonPropertyName("properties")] List<RawPythonProperty>? Properties,
    [property: JsonPropertyName("entryPoint")] bool? EntryPoint = null,
    [property: JsonPropertyName("reExportedFrom")] string? ReExportedFrom = null,
    [property: JsonPropertyName("deprecated")] bool? Deprecated = null,
    [property: JsonPropertyName("deprecatedMsg")] string? DeprecatedMsg = null
);

public sealed record RawPythonMethod(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("sig")] string? Sig,
    [property: JsonPropertyName("doc")] string? Doc,
    [property: JsonPropertyName("async")] bool? Async,
    [property: JsonPropertyName("classmethod")] bool? Classmethod,
    [property: JsonPropertyName("staticmethod")] bool? Staticmethod,
    [property: JsonPropertyName("ret")] string? Ret = null,
    [property: JsonPropertyName("deprecated")] bool? Deprecated = null,
    [property: JsonPropertyName("deprecatedMsg")] string? DeprecatedMsg = null
);

public sealed record RawPythonProperty(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("doc")] string? Doc,
    [property: JsonPropertyName("deprecated")] bool? Deprecated = null,
    [property: JsonPropertyName("deprecatedMsg")] string? DeprecatedMsg = null
);

public sealed record RawPythonFunction(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("sig")] string? Sig,
    [property: JsonPropertyName("doc")] string? Doc,
    [property: JsonPropertyName("async")] bool? Async,
    [property: JsonPropertyName("ret")] string? Ret = null,
    [property: JsonPropertyName("entryPoint")] bool? EntryPoint = null,
    [property: JsonPropertyName("reExportedFrom")] string? ReExportedFrom = null,
    [property: JsonPropertyName("deprecated")] bool? Deprecated = null,
    [property: JsonPropertyName("deprecatedMsg")] string? DeprecatedMsg = null
);

/// <summary>
/// Raw dependency info from Python extractor output.
/// </summary>
public sealed record RawPythonDependency(
    [property: JsonPropertyName("package")] string? Package,
    [property: JsonPropertyName("classes")] List<RawPythonClass>? Classes,
    [property: JsonPropertyName("functions")] List<RawPythonFunction>? Functions
);

#endregion
