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

    /// <summary>
    /// Analyzes API coverage across multiple packages in a monorepo.
    /// </summary>
    /// <param name="rootPath">Root path of the monorepo.</param>
    /// <param name="samplesPath">Optional samples folder override for all packages.</param>
    /// <param name="language">Optional language override.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Batch coverage analysis result.</returns>
    Task<CoverageBatchResult> AnalyzeCoverageMonorepoAsync(string rootPath, string? samplesPath = null, string? language = null, IProgress<string>? progress = null, CancellationToken ct = default);
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

    public async Task<CoverageBatchResult> AnalyzeCoverageMonorepoAsync(
        string rootPath,
        string? samplesPath,
        string? language,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (!Directory.Exists(rootPath))
        {
            return new CoverageBatchResult
            {
                Success = false,
                ErrorCode = "ROOT_NOT_FOUND",
                ErrorMessage = $"Root path not found: {rootPath}",
                RootPath = rootPath,
            };
        }

        var packages = FindMonorepoPackages(rootPath).ToArray();
        if (packages.Length == 0)
        {
            return new CoverageBatchResult
            {
                Success = false,
                ErrorCode = "NO_PACKAGES_FOUND",
                ErrorMessage = "No SDK packages found under the monorepo root.",
                RootPath = rootPath,
            };
        }

        var results = new List<CoverageBatchItem>(packages.Length);
        var totalOperations = 0;
        var covered = 0;
        var uncovered = 0;
        var analyzed = 0;
        var skipped = 0;
        var failed = 0;

        progress?.Report($"Found {packages.Length} packages under {rootPath}");

        for (var index = 0; index < packages.Length; index++)
        {
            ct.ThrowIfCancellationRequested();

            var packagePath = packages[index];
            var step = index + 1;

            var relativePath = Path.GetRelativePath(rootPath, packagePath).Replace("\\", "/");
            var samplesFolder = samplesPath;

            progress?.Report($"[{step}/{packages.Length}] {relativePath}: analyzing");

            if (string.IsNullOrWhiteSpace(samplesFolder))
            {
                var samples = await DetectSamplesFolderAsync(packagePath, ct);
                if (!samples.HasExistingSamples || string.IsNullOrWhiteSpace(samples.SamplesFolder))
                {
                    skipped++;
                    progress?.Report($"[{step}/{packages.Length}] {relativePath}: skipped (no samples)");
                    results.Add(new CoverageBatchItem
                    {
                        PackagePath = packagePath,
                        RelativePath = relativePath,
                        SamplesFolder = samples.SamplesFolder,
                        SkippedNoSamples = true,
                        Success = true,
                    });
                    continue;
                }

                samplesFolder = samples.SamplesFolder;
            }

            var analysis = await AnalyzeCoverageAsync(packagePath, samplesFolder, language, ct);
            if (!analysis.Success)
            {
                failed++;
                progress?.Report($"[{step}/{packages.Length}] {relativePath}: failed ({analysis.ErrorCode})");
                results.Add(new CoverageBatchItem
                {
                    PackagePath = packagePath,
                    RelativePath = relativePath,
                    SamplesFolder = samplesFolder,
                    Success = false,
                    ErrorCode = analysis.ErrorCode,
                    ErrorMessage = analysis.ErrorMessage,
                });
                continue;
            }

            analyzed++;
            totalOperations += analysis.TotalOperations;
            covered += analysis.CoveredCount;
            uncovered += analysis.UncoveredCount;

            progress?.Report($"[{step}/{packages.Length}] {relativePath}: {analysis.CoveredCount}/{analysis.TotalOperations} covered");

            results.Add(new CoverageBatchItem
            {
                PackagePath = packagePath,
                RelativePath = relativePath,
                SamplesFolder = samplesFolder,
                Success = true,
                Result = analysis,
            });
        }

        var coveragePercent = totalOperations > 0
            ? (double)covered / totalOperations * 100.0
            : 0.0;

        return new CoverageBatchResult
        {
            Success = true,
            RootPath = rootPath,
            TotalPackages = packages.Length,
            AnalyzedPackages = analyzed,
            SkippedPackages = skipped,
            FailedPackages = failed,
            TotalOperations = totalOperations,
            CoveredCount = covered,
            UncoveredCount = uncovered,
            CoveragePercent = coveragePercent,
            Packages = results.ToArray(),
        };
    }

    /// <summary>
    /// Discovers SDK packages in a monorepo structure.
    /// Uses a smart, defensive approach that:
    /// 1. Searches multiple potential root directories (sdk/, packages/, libs/, src/, or root itself)
    /// 2. Identifies packages by language-specific project markers
    /// 3. Finds the package root by walking up from the project file
    /// 4. Filters out test projects, examples, and build artifacts
    /// </summary>
    private static IEnumerable<string> FindMonorepoPackages(string rootPath)
    {
        // Track discovered packages to avoid duplicates
        var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Directories to skip entirely
        var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "node_modules", "vendor", ".git", "bin", "obj", "dist", "build",
            "__pycache__", ".tox", ".pytest_cache", "target", ".gradle",
            "packages", // NuGet packages folder
        };

        // Patterns that indicate a test/sample project (case-insensitive contains)
        var testPatterns = new[] { "test", "sample", "example", "demo", "benchmark", "perf" };

        // Language-specific project file patterns
        var projectPatterns = new[]
        {
            "package.json",      // TypeScript/JavaScript
            "*.csproj",          // .NET C#
            "*.fsproj",          // .NET F#
            "pyproject.toml",    // Python (modern)
            "setup.py",          // Python (legacy)
            "go.mod",            // Go
            "pom.xml",           // Java Maven
            "build.gradle",      // Java Gradle
            "build.gradle.kts",  // Java Gradle Kotlin DSL
            "Cargo.toml",        // Rust
        };

        // Potential monorepo package roots to search
        var searchRoots = new List<string>();

        // Check for common monorepo structures
        var sdkDir = Path.Combine(rootPath, "sdk");
        var packagesDir = Path.Combine(rootPath, "packages");
        var libsDir = Path.Combine(rootPath, "libs");
        var srcDir = Path.Combine(rootPath, "src");

        if (Directory.Exists(sdkDir))
            searchRoots.Add(sdkDir);
        if (Directory.Exists(packagesDir))
            searchRoots.Add(packagesDir);
        if (Directory.Exists(libsDir))
            searchRoots.Add(libsDir);

        // If none of the standard roots exist, search from the root itself
        if (searchRoots.Count == 0)
        {
            // Check if src/ contains multiple packages or is itself a package
            if (Directory.Exists(srcDir))
            {
                // If src/ has subdirectories with project files, treat src/ as monorepo root
                var srcSubdirs = Directory.GetDirectories(srcDir);
                if (srcSubdirs.Length > 1)
                    searchRoots.Add(srcDir);
            }

            // Fall back to root path
            if (searchRoots.Count == 0)
                searchRoots.Add(rootPath);
        }

        foreach (var searchRoot in searchRoots)
        {
            foreach (var pattern in projectPatterns)
            {
                IEnumerable<string> projectFiles;
                try
                {
                    projectFiles = Directory.EnumerateFiles(searchRoot, pattern, SearchOption.AllDirectories);
                }
                catch (Exception)
                {
                    // Permission denied or other IO error - skip this pattern
                    continue;
                }

                foreach (var projectFile in projectFiles)
                {
                    // Skip files in excluded directories
                    var relativePath = Path.GetRelativePath(searchRoot, projectFile);
                    var pathSegments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                    if (pathSegments.Any(seg => skipDirs.Contains(seg)))
                        continue;

                    // Skip test/sample projects
                    var fileName = Path.GetFileNameWithoutExtension(projectFile);
                    var dirName = Path.GetFileName(Path.GetDirectoryName(projectFile) ?? "");
                    if (testPatterns.Any(p =>
                        fileName.Contains(p, StringComparison.OrdinalIgnoreCase) ||
                        dirName.Contains(p, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    // Find the package root directory
                    var packageDir = FindPackageRoot(projectFile, searchRoot, pattern);
                    if (packageDir is null || discovered.Contains(packageDir))
                        continue;

                    // Validate it's a real package directory
                    if (!Directory.Exists(packageDir))
                        continue;

                    // Skip if it's the search root itself (avoid treating entire repo as one package)
                    if (string.Equals(packageDir, searchRoot, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(packageDir, rootPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    discovered.Add(packageDir);
                    yield return packageDir;
                }
            }
        }
    }

    /// <summary>
    /// Finds the package root directory from a project file.
    /// Walks up from the project file to find the logical package boundary.
    /// </summary>
    private static string? FindPackageRoot(string projectFile, string searchRoot, string pattern)
    {
        var projectDir = Path.GetDirectoryName(projectFile);
        if (projectDir is null)
            return null;

        // For most project types, the project file is at the package root
        // Exception: .NET projects are often in src/ subdirectory
        if (pattern.EndsWith(".csproj") || pattern.EndsWith(".fsproj"))
        {
            // Check if we're in a src/ subdirectory
            var parentDir = Path.GetDirectoryName(projectDir);
            if (parentDir != null)
            {
                var parentName = Path.GetFileName(projectDir);
                if (string.Equals(parentName, "src", StringComparison.OrdinalIgnoreCase))
                {
                    // The package root is one level up from src/
                    return parentDir;
                }
            }
        }

        // For package.json, check if this is a workspace root (has workspaces field)
        if (pattern == "package.json")
        {
            try
            {
                var content = File.ReadAllText(projectFile);
                // Skip workspace root package.json files
                if (content.Contains("\"workspaces\"") || content.Contains("\"private\": true"))
                {
                    // Could be a workspace root - check if it has meaningful exports
                    if (!content.Contains("\"main\"") && !content.Contains("\"exports\"") && !content.Contains("\"types\""))
                    {
                        return null;
                    }
                }
            }
            catch
            {
                // Ignore read errors
            }
        }

        // The project directory is the package root
        return projectDir;
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

/// <summary>Result of monorepo coverage analysis.</summary>
public sealed record CoverageBatchResult
{
    public required bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string? RootPath { get; init; }
    public int TotalPackages { get; init; }
    public int AnalyzedPackages { get; init; }
    public int SkippedPackages { get; init; }
    public int FailedPackages { get; init; }
    public int TotalOperations { get; init; }
    public int CoveredCount { get; init; }
    public int UncoveredCount { get; init; }
    public double CoveragePercent { get; init; }
    public CoverageBatchItem[]? Packages { get; init; }
}

/// <summary>Per-package coverage analysis result.</summary>
public sealed record CoverageBatchItem
{
    public required string PackagePath { get; init; }
    public required string RelativePath { get; init; }
    public string? SamplesFolder { get; init; }
    public bool SkippedNoSamples { get; init; }
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public CoverageAnalysisResult? Result { get; init; }
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
[JsonSerializable(typeof(CoverageBatchResult))]
[JsonSerializable(typeof(CoverageBatchItem))]
[JsonSerializable(typeof(CoveredOperationInfo[]))]
[JsonSerializable(typeof(UncoveredOperationInfo[]))]
public sealed partial class PackageInfoJsonContext : JsonSerializerContext
{
}
