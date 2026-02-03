// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;
using ApiExtractor.Contracts;
using ApiExtractor.DotNet;
using ApiExtractor.Go;
using ApiExtractor.Java;
using ApiExtractor.Python;
using ApiExtractor.TypeScript;
using Microsoft.SdkChat.Models;

namespace Microsoft.SdkChat.Services;

/// <summary>
/// Interface for SDK package analysis operations.
/// Enables dependency injection and mocking for tests.
/// </summary>
public interface IPackageInfoService
{
    /// <summary>
    /// Detects the source folder for an SDK package.
    /// </summary>
    /// <param name="packagePath">Root path of the SDK package.</param>
    /// <param name="language">Optional language override (e.g., "dotnet", "python").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Source folder detection result.</returns>
    Task<SourceFolderResult> DetectSourceFolderAsync(string packagePath, string? language = null, CancellationToken ct = default);

    /// <summary>
    /// Detects the samples folder for an SDK package.
    /// </summary>
    /// <param name="packagePath">Root path of the SDK package.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Samples folder detection result.</returns>
    Task<SamplesFolderResult> DetectSamplesFolderAsync(string packagePath, CancellationToken ct = default);

    /// <summary>
    /// Extracts the public API surface from an SDK package.
    /// </summary>
    /// <param name="packagePath">Root path of the SDK package.</param>
    /// <param name="language">Optional language override.</param>
    /// <param name="asJson">If true, returns JSON format; otherwise returns language stubs.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>API extraction result with the public API surface.</returns>
    Task<ApiExtractionResult> ExtractPublicApiAsync(string packagePath, string? language = null, bool asJson = false, CancellationToken ct = default);

    /// <summary>
    /// Analyzes API coverage in existing samples or tests.
    /// </summary>
    /// <param name="packagePath">Root path of the SDK package.</param>
    /// <param name="samplesPath">Optional path to samples folder (auto-detected if null).</param>
    /// <param name="language">Optional language override.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Coverage analysis result with covered and uncovered operations.</returns>
    Task<CoverageAnalysisResult> AnalyzeCoverageAsync(string packagePath, string? samplesPath = null, string? language = null, CancellationToken ct = default);
}

/// <summary>
/// Unified service for SDK package analysis: source detection, samples detection,
/// API extraction, and coverage analysis.
/// </summary>
public sealed class PackageInfoService : IPackageInfoService
{
    /// <inheritdoc />
    public async Task<SourceFolderResult> DetectSourceFolderAsync(string packagePath, string? language = null, CancellationToken ct = default)
    {
        var sdkInfo = await SdkInfo.ScanAsync(packagePath, ct).ConfigureAwait(false);

        var effectiveLanguage = !string.IsNullOrEmpty(language)
            ? SdkLanguageHelpers.Parse(language)
            : sdkInfo.Language;

        return new SourceFolderResult
        {
            SourceFolder = sdkInfo.SourceFolder,
            Language = effectiveLanguage?.ToString(),
            LanguageName = sdkInfo.LanguageName,
            FileExtension = sdkInfo.FileExtension,
            RootPath = sdkInfo.RootPath,
            SdkName = sdkInfo.SdkName,
            IsValid = sdkInfo.IsValid
        };
    }

    /// <inheritdoc />
    public async Task<SamplesFolderResult> DetectSamplesFolderAsync(string packagePath, CancellationToken ct = default)
    {
        var sdkInfo = await SdkInfo.ScanAsync(packagePath, ct).ConfigureAwait(false);

        return new SamplesFolderResult
        {
            SamplesFolder = sdkInfo.SamplesFolder,
            SuggestedSamplesFolder = sdkInfo.SuggestedSamplesFolder,
            AllCandidates = sdkInfo.AllSamplesCandidates.ToArray(),
            HasExistingSamples = sdkInfo.SamplesFolder != null,
            Language = sdkInfo.Language?.ToString(),
            RootPath = sdkInfo.RootPath
        };
    }

    /// <inheritdoc />
    public async Task<ApiExtractionResult> ExtractPublicApiAsync(
        string packagePath,
        string? language = null,
        bool asJson = false,
        CancellationToken ct = default)
    {
        var sdkInfo = await SdkInfo.ScanAsync(packagePath, ct).ConfigureAwait(false);

        var effectiveLanguage = !string.IsNullOrEmpty(language)
            ? SdkLanguageHelpers.Parse(language)
            : sdkInfo.Language;

        if (effectiveLanguage == null || effectiveLanguage == SdkLanguage.Unknown)
        {
            return new ApiExtractionResult
            {
                Success = false,
                ErrorCode = "LANGUAGE_DETECTION_FAILED",
                ErrorMessage = "Could not detect SDK language. Specify --language explicitly.",
                SourceFolder = sdkInfo.SourceFolder
            };
        }

        var extractor = CreateExtractor(effectiveLanguage.Value);
        if (extractor == null)
        {
            return new ApiExtractionResult
            {
                Success = false,
                ErrorCode = "EXTRACTOR_NOT_FOUND",
                ErrorMessage = $"No extractor available for language: {effectiveLanguage}",
                SourceFolder = sdkInfo.SourceFolder
            };
        }

        if (!extractor.IsAvailable())
        {
            return new ApiExtractionResult
            {
                Success = false,
                ErrorCode = "EXTRACTOR_UNAVAILABLE",
                ErrorMessage = extractor.UnavailableReason ?? $"Extractor for {effectiveLanguage} is not available",
                SourceFolder = sdkInfo.SourceFolder
            };
        }

        var result = await extractor.ExtractAsyncCore(sdkInfo.SourceFolder, ct).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            var failure = (ExtractorResult.Failure)result;
            return new ApiExtractionResult
            {
                Success = false,
                ErrorCode = "EXTRACTION_FAILED",
                ErrorMessage = failure.Error,
                SourceFolder = sdkInfo.SourceFolder
            };
        }

        var apiIndex = result.GetValueOrThrow();

        return new ApiExtractionResult
        {
            Success = true,
            SourceFolder = sdkInfo.SourceFolder,
            Language = effectiveLanguage.Value.ToString(),
            Package = apiIndex.Package,
            ApiSurface = asJson ? apiIndex.ToJson(pretty: true) : apiIndex.ToStubs(),
            Format = asJson ? "json" : "stubs",
            Warnings = result.Warnings.ToArray()
        };
    }

    /// <inheritdoc />
    public async Task<CoverageAnalysisResult> AnalyzeCoverageAsync(
        string packagePath,
        string? samplesPath = null,
        string? language = null,
        CancellationToken ct = default)
    {
        var sdkInfo = await SdkInfo.ScanAsync(packagePath, ct).ConfigureAwait(false);

        var effectiveLanguage = !string.IsNullOrEmpty(language)
            ? SdkLanguageHelpers.Parse(language)
            : sdkInfo.Language;

        if (effectiveLanguage == null || effectiveLanguage == SdkLanguage.Unknown)
        {
            return new CoverageAnalysisResult
            {
                Success = false,
                ErrorCode = "LANGUAGE_DETECTION_FAILED",
                ErrorMessage = "Could not detect SDK language. Specify --language explicitly."
            };
        }

        var effectiveSamplesPath = samplesPath ?? sdkInfo.SamplesFolder;
        if (string.IsNullOrEmpty(effectiveSamplesPath) || !Directory.Exists(effectiveSamplesPath))
        {
            return new CoverageAnalysisResult
            {
                Success = false,
                ErrorCode = "NO_SAMPLES_FOUND",
                ErrorMessage = $"No samples folder found. Detected path: {sdkInfo.SuggestedSamplesFolder}"
            };
        }

        // Extract API first
        var apiResult = await ExtractPublicApiAsync(packagePath, language, asJson: false, ct).ConfigureAwait(false);
        if (!apiResult.Success)
        {
            return new CoverageAnalysisResult
            {
                Success = false,
                ErrorCode = apiResult.ErrorCode,
                ErrorMessage = apiResult.ErrorMessage
            };
        }

        // Analyze usage
        var (covered, uncovered, totalOperations) = await AnalyzeUsageInternalAsync(
            effectiveLanguage.Value, sdkInfo.SourceFolder, effectiveSamplesPath, ct).ConfigureAwait(false);

        var coveragePercent = totalOperations > 0 ? (covered.Count * 100.0 / totalOperations) : 0;

        return new CoverageAnalysisResult
        {
            Success = true,
            SourceFolder = sdkInfo.SourceFolder,
            SamplesFolder = effectiveSamplesPath,
            Language = effectiveLanguage.Value.ToString(),
            TotalOperations = totalOperations,
            CoveredCount = covered.Count,
            UncoveredCount = uncovered.Count,
            CoveragePercent = Math.Round(coveragePercent, 1),
            CoveredOperations = covered.ToArray(),
            UncoveredOperations = uncovered.ToArray()
        };
    }

    /// <summary>
    /// Registry of language-specific extractor and analyzer factories.
    /// Adding a new language requires only adding an entry here.
    /// </summary>
    private static readonly Dictionary<SdkLanguage, Func<(IApiExtractor Extractor, IUsageAnalyzer Analyzer)>> AnalyzerRegistry = new()
    {
        [SdkLanguage.DotNet] = () => (new CSharpApiExtractor(), new CSharpUsageAnalyzer()),
        [SdkLanguage.Python] = () => (new PythonApiExtractor(), new PythonUsageAnalyzer()),
        [SdkLanguage.Go] = () => (new GoApiExtractor(), new GoUsageAnalyzer()),
        [SdkLanguage.TypeScript] = () => (new TypeScriptApiExtractor(), new TypeScriptUsageAnalyzer()),
        [SdkLanguage.JavaScript] = () => (new TypeScriptApiExtractor(), new TypeScriptUsageAnalyzer()), // JS uses TS tooling
        [SdkLanguage.Java] = () => (new JavaApiExtractor(), new JavaUsageAnalyzer()),
    };

    private async Task<(List<CoveredOperationInfo> Covered, List<UncoveredOperationInfo> Uncovered, int Total)> AnalyzeUsageInternalAsync(
        SdkLanguage language,
        string sourcePath,
        string samplesPath,
        CancellationToken ct)
    {
        // Check if language is supported
        if (!AnalyzerRegistry.TryGetValue(language, out var factory))
            return ([], [], 0);

        // Create extractor and analyzer from registry
        var (extractor, analyzer) = factory();

        // Check availability
        if (!extractor.IsAvailable())
            return ([], [], 0);

        // Extract API surface
        var extractResult = await extractor.ExtractAsyncCore(sourcePath, ct).ConfigureAwait(false);
        if (!extractResult.IsSuccess)
            return ([], [], 0);

        var apiIndex = extractResult.GetValueOrThrow();

        // Analyze usage with the non-generic interface
        var usage = await analyzer.AnalyzeAsyncCore(samplesPath, apiIndex, ct).ConfigureAwait(false);

        // Map to output format
        var total = usage.CoveredOperations.Count + usage.UncoveredOperations.Count;

        var covered = usage.CoveredOperations
            .Select(o => new CoveredOperationInfo
            {
                ClientType = o.ClientType,
                Operation = o.Operation,
                File = o.File,
                Line = o.Line
            })
            .ToList();

        var uncovered = usage.UncoveredOperations
            .Select(o => new UncoveredOperationInfo
            {
                ClientType = o.ClientType,
                Operation = o.Operation,
                Signature = o.Signature
            })
            .ToList();

        return (covered, uncovered, total);
    }

    private static IApiExtractor? CreateExtractor(SdkLanguage language)
    {
        if (AnalyzerRegistry.TryGetValue(language, out var factory))
            return factory().Extractor;
        return null;
    }
}

/// <summary>Result of source folder detection.</summary>
public sealed record SourceFolderResult
{
    public required string SourceFolder { get; init; }
    public string? Language { get; init; }
    public string? LanguageName { get; init; }
    public string? FileExtension { get; init; }
    public required string RootPath { get; init; }
    public required string SdkName { get; init; }
    public bool IsValid { get; init; }
}

/// <summary>Result of samples folder detection.</summary>
public sealed record SamplesFolderResult
{
    public string? SamplesFolder { get; init; }
    public required string SuggestedSamplesFolder { get; init; }
    public required string[] AllCandidates { get; init; }
    public bool HasExistingSamples { get; init; }
    public string? Language { get; init; }
    public required string RootPath { get; init; }
}

/// <summary>Result of API extraction.</summary>
public sealed record ApiExtractionResult
{
    public required bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string? SourceFolder { get; init; }
    public string? Language { get; init; }
    public string? Package { get; init; }
    public string? ApiSurface { get; init; }
    public string? Format { get; init; }
    public string[]? Warnings { get; init; }
}

/// <summary>Result of coverage analysis.</summary>
public sealed record CoverageAnalysisResult
{
    public required bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string? SourceFolder { get; init; }
    public string? SamplesFolder { get; init; }
    public string? Language { get; init; }
    public int TotalOperations { get; init; }
    public int CoveredCount { get; init; }
    public int UncoveredCount { get; init; }
    public double CoveragePercent { get; init; }
    public CoveredOperationInfo[]? CoveredOperations { get; init; }
    public UncoveredOperationInfo[]? UncoveredOperations { get; init; }
}

/// <summary>A covered API operation.</summary>
public sealed record CoveredOperationInfo
{
    public required string ClientType { get; init; }
    public required string Operation { get; init; }
    public required string File { get; init; }
    public required int Line { get; init; }
}

/// <summary>An uncovered API operation.</summary>
public sealed record UncoveredOperationInfo
{
    public required string ClientType { get; init; }
    public required string Operation { get; init; }
    public required string Signature { get; init; }
}

/// <summary>
/// Source-generated JSON context for package info result types.
/// Used by CLI commands and MCP tools for serialization.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(SourceFolderResult))]
[JsonSerializable(typeof(SamplesFolderResult))]
[JsonSerializable(typeof(ApiExtractionResult))]
[JsonSerializable(typeof(CoverageAnalysisResult))]
[JsonSerializable(typeof(CoveredOperationInfo[]))]
[JsonSerializable(typeof(UncoveredOperationInfo[]))]
public sealed partial class PackageInfoJsonContext : JsonSerializerContext
{
}
