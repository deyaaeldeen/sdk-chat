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
