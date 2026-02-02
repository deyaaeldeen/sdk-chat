// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;

namespace ApiExtractor.Contracts;

/// <summary>
/// Cached JsonSerializerOptions instances to avoid repeated allocations.
/// Each instantiation of JsonSerializerOptions allocates ~1KB and triggers JIT compilation.
/// </summary>
public static class JsonOptionsCache
{
    /// <summary>
    /// Options for case-insensitive property name matching during deserialization.
    /// </summary>
    public static JsonSerializerOptions CaseInsensitive { get; } = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Options for human-readable indented output during serialization.
    /// </summary>
    public static JsonSerializerOptions Indented { get; } = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Options combining indented output with case-insensitive reading.
    /// </summary>
    public static JsonSerializerOptions IndentedCaseInsensitive { get; } = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
}
