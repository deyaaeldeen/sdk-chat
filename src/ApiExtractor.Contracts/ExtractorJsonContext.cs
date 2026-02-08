// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApiExtractor.Contracts;

/// <summary>
/// Centralized source-generated JSON serializer context for all API extractors.
/// Provides AOT-compatible, reflection-free JSON serialization with improved performance.
/// 
/// This context consolidates all extractor DTOs to:
/// - Eliminate duplicate source generator passes across extractor projects
/// - Provide consistent JSON handling across all languages
/// - Enable zero-suppression AOT compliance
/// 
/// Usage:
///   JsonSerializer.Serialize(index, ExtractorJsonContext.Default.UsageResult);
///   JsonSerializer.Deserialize(json, ExtractorJsonContext.Default.UsageResult);
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]

// Usage analyzer DTOs - shared across all language extractors
[JsonSerializable(typeof(UsageResult))]
[JsonSerializable(typeof(CoveredOp))]
[JsonSerializable(typeof(UncoveredOp))]
[JsonSerializable(typeof(List<CoveredOp>))]
[JsonSerializable(typeof(List<UncoveredOp>))]
[JsonSerializable(typeof(List<string>))]

[JsonSerializable(typeof(RawPythonApiIndex))]
[JsonSerializable(typeof(RawPythonModule))]
[JsonSerializable(typeof(RawPythonClass))]
[JsonSerializable(typeof(RawPythonMethod))]
[JsonSerializable(typeof(RawPythonProperty))]
[JsonSerializable(typeof(RawPythonFunction))]

public partial class ExtractorJsonContext : JsonSerializerContext
{
    private static ExtractorJsonContext? _indented;

    /// <summary>Context configured for indented (pretty) output.</summary>
    public static ExtractorJsonContext Indented => _indented ??= new ExtractorJsonContext(
        new JsonSerializerOptions(Default.Options) { WriteIndented = true });
}

#region Usage Analyzer DTOs

/// <summary>
/// Result from usage analysis scripts (Python/Go/Java/TypeScript).
/// Matches JSON output from extract_api.py, extract_api.go, etc.
/// </summary>
public sealed record UsageResult(
    [property: JsonPropertyName("fileCount")] int FileCount,
    [property: JsonPropertyName("covered")] List<CoveredOp>? Covered,
    [property: JsonPropertyName("uncovered")] List<UncoveredOp>? Uncovered,
    [property: JsonPropertyName("patterns")] List<string>? Patterns
);

/// <summary>
/// A covered (used) API operation found in analyzed code.
/// </summary>
public sealed record CoveredOp(
    [property: JsonPropertyName("client")] string? Client,
    [property: JsonPropertyName("method")] string? Method,
    [property: JsonPropertyName("file")] string? File,
    [property: JsonPropertyName("line")] int Line
);

/// <summary>
/// An uncovered (unused) API operation from the SDK.
/// </summary>
public sealed record UncoveredOp(
    [property: JsonPropertyName("client")] string? Client,
    [property: JsonPropertyName("method")] string? Method,
    [property: JsonPropertyName("sig")] string? Sig
);

#endregion

#region Python Raw API Models

public sealed record RawPythonApiIndex(
    [property: JsonPropertyName("package")] string? Package,
    [property: JsonPropertyName("modules")] List<RawPythonModule>? Modules
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
    [property: JsonPropertyName("properties")] List<RawPythonProperty>? Properties
);

public sealed record RawPythonMethod(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("sig")] string? Sig,
    [property: JsonPropertyName("doc")] string? Doc,
    [property: JsonPropertyName("async")] bool? Async,
    [property: JsonPropertyName("classmethod")] bool? Classmethod,
    [property: JsonPropertyName("staticmethod")] bool? Staticmethod
);

public sealed record RawPythonProperty(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("doc")] string? Doc
);

public sealed record RawPythonFunction(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("sig")] string? Sig,
    [property: JsonPropertyName("doc")] string? Doc,
    [property: JsonPropertyName("async")] bool? Async
);

#endregion
