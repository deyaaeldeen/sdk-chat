// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace ApiExtractor.Contracts;

/// <summary>
/// Analyzes code to extract which API operations are being used.
/// Generic across samples, tests, or any consumer code.
/// </summary>
/// <typeparam name="TApiIndex">The API index type to match against.</typeparam>
public interface IUsageAnalyzer<TApiIndex> where TApiIndex : class
{
    /// <summary>
    /// Gets the language this analyzer supports.
    /// </summary>
    string Language { get; }

    /// <summary>
    /// Analyzes code files to extract API usage patterns.
    /// </summary>
    /// <param name="codePath">Path to directory containing code to analyze.</param>
    /// <param name="apiIndex">The API surface to match usages against.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Extracted usage information.</returns>
    Task<UsageIndex> AnalyzeAsync(string codePath, TApiIndex apiIndex, CancellationToken ct = default);

    /// <summary>
    /// Formats the usage index as a compact summary for LLM context.
    /// </summary>
    string Format(UsageIndex index);
}

/// <summary>
/// Extracted API usage information from consumer code.
/// </summary>
public sealed record UsageIndex
{
    /// <summary>Total number of files analyzed.</summary>
    public required int FileCount { get; init; }

    /// <summary>Operations that are covered (called) in the analyzed code.</summary>
    public List<OperationUsage> CoveredOperations { get; init; } = [];

    /// <summary>Operations from the API that are NOT used in the analyzed code.</summary>
    public List<UncoveredOperation> UncoveredOperations { get; init; } = [];
}

/// <summary>
/// A single API operation usage found in consumer code.
/// </summary>
public sealed record OperationUsage
{
    /// <summary>The client/service type being called.</summary>
    public required string ClientType { get; init; }

    /// <summary>The method/operation name.</summary>
    public required string Operation { get; init; }

    /// <summary>Relative path to the file containing this usage.</summary>
    public required string File { get; init; }

    /// <summary>Line number where the call occurs.</summary>
    public required int Line { get; init; }
}

/// <summary>
/// An API operation that exists but has no usage in analyzed code.
/// </summary>
public sealed record UncoveredOperation
{
    /// <summary>The client/service type.</summary>
    public required string ClientType { get; init; }

    /// <summary>The method/operation name.</summary>
    public required string Operation { get; init; }

    /// <summary>Brief signature for context.</summary>
    public required string Signature { get; init; }
}
