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

    private async Task<(List<CoveredOperationInfo> Covered, List<UncoveredOperationInfo> Uncovered, int Total)> AnalyzeUsageInternalAsync(
        SdkLanguage language,
        string sourcePath,
        string samplesPath,
        CancellationToken ct)
    {
        List<CoveredOperationInfo> covered = [];
        List<UncoveredOperationInfo> uncovered = [];
        var total = 0;

        // Language-specific analysis
        switch (language)
        {
            case SdkLanguage.DotNet:
                var csharpExtractor = new CSharpApiExtractor();
                var csharpAnalyzer = new CSharpUsageAnalyzer();
                var csharpApi = await csharpExtractor.ExtractAsync(sourcePath, ct).ConfigureAwait(false);
                var csharpUsage = await csharpAnalyzer.AnalyzeAsync(samplesPath, csharpApi, ct).ConfigureAwait(false);

                total = csharpUsage.CoveredOperations.Count + csharpUsage.UncoveredOperations.Count;
                covered = csharpUsage.CoveredOperations
                    .Select(o => new CoveredOperationInfo { ClientType = o.ClientType, Operation = o.Operation, File = o.File, Line = o.Line })
                    .ToList();
                uncovered = csharpUsage.UncoveredOperations
                    .Select(o => new UncoveredOperationInfo { ClientType = o.ClientType, Operation = o.Operation, Signature = o.Signature })
                    .ToList();
                break;

            case SdkLanguage.Python:
                var pythonExtractor = new PythonApiExtractor();
                if (!pythonExtractor.IsAvailable())
                {
                    return ([], [], 0);
                }
                var pythonResult = await ((IApiExtractor<ApiExtractor.Python.ApiIndex>)pythonExtractor).ExtractAsync(sourcePath, ct).ConfigureAwait(false);
                if (!pythonResult.IsSuccess) return ([], [], 0);
                var pythonApi = (ApiExtractor.Python.ApiIndex)pythonResult.GetValueOrThrow();
                var pythonAnalyzer = new PythonUsageAnalyzer();
                var pythonUsage = await pythonAnalyzer.AnalyzeAsync(samplesPath, pythonApi, ct).ConfigureAwait(false);

                total = pythonUsage.CoveredOperations.Count + pythonUsage.UncoveredOperations.Count;
                covered = pythonUsage.CoveredOperations
                    .Select(o => new CoveredOperationInfo { ClientType = o.ClientType, Operation = o.Operation, File = o.File, Line = o.Line })
                    .ToList();
                uncovered = pythonUsage.UncoveredOperations
                    .Select(o => new UncoveredOperationInfo { ClientType = o.ClientType, Operation = o.Operation, Signature = o.Signature })
                    .ToList();
                break;

            case SdkLanguage.Go:
                var goExtractor = new GoApiExtractor();
                if (!goExtractor.IsAvailable())
                {
                    return ([], [], 0);
                }
                var goResult = await ((IApiExtractor<ApiExtractor.Go.ApiIndex>)goExtractor).ExtractAsync(sourcePath, ct).ConfigureAwait(false);
                if (!goResult.IsSuccess) return ([], [], 0);
                var goApi = (ApiExtractor.Go.ApiIndex)goResult.GetValueOrThrow();
                var goAnalyzer = new GoUsageAnalyzer();
                var goUsage = await goAnalyzer.AnalyzeAsync(samplesPath, goApi, ct).ConfigureAwait(false);

                total = goUsage.CoveredOperations.Count + goUsage.UncoveredOperations.Count;
                covered = goUsage.CoveredOperations
                    .Select(o => new CoveredOperationInfo { ClientType = o.ClientType, Operation = o.Operation, File = o.File, Line = o.Line })
                    .ToList();
                uncovered = goUsage.UncoveredOperations
                    .Select(o => new UncoveredOperationInfo { ClientType = o.ClientType, Operation = o.Operation, Signature = o.Signature })
                    .ToList();
                break;

            case SdkLanguage.TypeScript:
            case SdkLanguage.JavaScript:
                var tsExtractor = new TypeScriptApiExtractor();
                if (!tsExtractor.IsAvailable())
                {
                    return ([], [], 0);
                }
                var tsResult = await ((IApiExtractor<ApiExtractor.TypeScript.ApiIndex>)tsExtractor).ExtractAsync(sourcePath, ct).ConfigureAwait(false);
                if (!tsResult.IsSuccess) return ([], [], 0);
                var tsApi = (ApiExtractor.TypeScript.ApiIndex)tsResult.GetValueOrThrow();
                var tsAnalyzer = new TypeScriptUsageAnalyzer();
                var tsUsage = await tsAnalyzer.AnalyzeAsync(samplesPath, tsApi, ct).ConfigureAwait(false);

                total = tsUsage.CoveredOperations.Count + tsUsage.UncoveredOperations.Count;
                covered = tsUsage.CoveredOperations
                    .Select(o => new CoveredOperationInfo { ClientType = o.ClientType, Operation = o.Operation, File = o.File, Line = o.Line })
                    .ToList();
                uncovered = tsUsage.UncoveredOperations
                    .Select(o => new UncoveredOperationInfo { ClientType = o.ClientType, Operation = o.Operation, Signature = o.Signature })
                    .ToList();
                break;

            case SdkLanguage.Java:
                var javaExtractor = new JavaApiExtractor();
                if (!javaExtractor.IsAvailable())
                {
                    return ([], [], 0);
                }
                var javaResult = await ((IApiExtractor<ApiExtractor.Java.ApiIndex>)javaExtractor).ExtractAsync(sourcePath, ct).ConfigureAwait(false);
                if (!javaResult.IsSuccess) return ([], [], 0);
                var javaApi = (ApiExtractor.Java.ApiIndex)javaResult.GetValueOrThrow();
                var javaAnalyzer = new JavaUsageAnalyzer();
                var javaUsage = await javaAnalyzer.AnalyzeAsync(samplesPath, javaApi, ct).ConfigureAwait(false);

                total = javaUsage.CoveredOperations.Count + javaUsage.UncoveredOperations.Count;
                covered = javaUsage.CoveredOperations
                    .Select(o => new CoveredOperationInfo { ClientType = o.ClientType, Operation = o.Operation, File = o.File, Line = o.Line })
                    .ToList();
                uncovered = javaUsage.UncoveredOperations
                    .Select(o => new UncoveredOperationInfo { ClientType = o.ClientType, Operation = o.Operation, Signature = o.Signature })
                    .ToList();
                break;
        }

        return (covered, uncovered, total);
    }

    private static IApiExtractor? CreateExtractor(SdkLanguage language) => language switch
    {
        SdkLanguage.DotNet => new CSharpApiExtractor(),
        SdkLanguage.Python => new PythonApiExtractor(),
        SdkLanguage.Go => new GoApiExtractor(),
        SdkLanguage.TypeScript => new TypeScriptApiExtractor(),
        SdkLanguage.JavaScript => new TypeScriptApiExtractor(), // JS uses TS extractor
        SdkLanguage.Java => new JavaApiExtractor(),
        _ => null
    };
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
