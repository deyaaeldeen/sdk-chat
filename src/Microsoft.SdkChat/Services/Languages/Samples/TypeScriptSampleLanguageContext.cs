// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using ApiExtractor.Contracts;
using ApiExtractor.TypeScript;
using Microsoft.SdkChat.Helpers;
using Microsoft.SdkChat.Models;

namespace Microsoft.SdkChat.Services.Languages.Samples;

public sealed class TypeScriptSampleLanguageContext : SampleLanguageContext
{
    private readonly TypeScriptUsageAnalyzer _usageAnalyzer = new();

    // Cached API index for reuse
    private ApiIndex? _cachedApiIndex;
    private string? _cachedSourcePath;

    public TypeScriptSampleLanguageContext(FileHelper fileHelper) : base(fileHelper) { }

    public override SdkLanguage Language => SdkLanguage.TypeScript;

    protected override string[] DefaultIncludeExtensions => new[] { ".ts" };

    protected override string[] DefaultExcludePatterns => new[]
    {
        "**/node_modules/**",
        "**/dist/**",
        "**/*.d.ts",
        "**/*.test.ts",
        "**/*.spec.ts"
    };

    protected override int GetPriority(FileMetadata file)
    {
        var path = file.RelativePath.Replace('\\', '/').ToLowerInvariant();
        var name = Path.GetFileName(file.FilePath).ToLowerInvariant();

        // Deprioritize generated code - load human-written code first
        var isGenerated = path.Contains("/generated/") ||
                          name.Contains(".generated.");
        var basePriority = isGenerated ? 100 : 0;

        // Within each category, prioritize key files
        if (name.Contains("client")) return basePriority + 1;
        if (name.Contains("model")) return basePriority + 2;
        if (name == "index.ts") return basePriority + 3;
        return basePriority + 10;
    }

    public override string GetInstructions() =>
        "TypeScript: ES modules, async/await, strict types, const/let, arrow functions.";

    /// <summary>
    /// Analyzes existing code to extract API usage patterns.
    /// </summary>
    public override async Task<UsageIndex?> AnalyzeUsageAsync(
        string sourcePath,
        string codePath,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(codePath))
            return null;

        var apiIndex = await GetOrExtractApiIndexAsync(sourcePath, ct);
        if (apiIndex == null)
            return null;

        return await _usageAnalyzer.AnalyzeAsync(codePath, apiIndex, ct);
    }

    public override string FormatUsage(UsageIndex usage) => _usageAnalyzer.Format(usage);

    /// <summary>
    /// Streams API surface with coverage analysis merged for ~70% token savings.
    /// Shows compact summary of covered operations and full signatures only for uncovered APIs.
    /// </summary>
    public override async IAsyncEnumerable<string> StreamContextAsync(
        string sourcePath,
        string? samplesPath,
        SdkChatConfig? config = null,
        int totalBudget = SampleConstants.DefaultContextCharacters,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Extract API surface
        var apiIndex = await GetOrExtractApiIndexAsync(sourcePath, ct);

        if (apiIndex == null)
        {
            // Fall back to base implementation
            await foreach (var chunk in base.StreamContextAsync(sourcePath, samplesPath, config, totalBudget, ct))
                yield return chunk;
            yield break;
        }

        // Analyze coverage if samples exist
        UsageIndex? coverage = null;
        if (!string.IsNullOrEmpty(samplesPath) && Directory.Exists(samplesPath))
        {
            coverage = await _usageAnalyzer.AnalyzeAsync(samplesPath, apiIndex, ct);
        }

        var maxLength = totalBudget - 100;
        string apiSurface;

        if (coverage != null && (coverage.CoveredOperations.Count > 0 || coverage.UncoveredOperations.Count > 0))
        {
            // Use coverage-aware formatting - merged and compact
            apiSurface = TypeScriptFormatter.FormatWithCoverage(apiIndex, coverage, maxLength);
        }
        else
        {
            // No coverage data - fall back to standard format
            apiSurface = TypeScriptFormatter.Format(apiIndex, maxLength);
        }

        // Yield unified API surface with coverage annotations
        yield return $"<api-surface package=\"{apiIndex.Package}\">\n";
        yield return apiSurface;
        yield return "</api-surface>\n";
    }

    private async Task<ApiIndex?> GetOrExtractApiIndexAsync(string sourcePath, CancellationToken ct)
    {
        var normalizedPath = Path.GetFullPath(sourcePath);

        if (_cachedApiIndex != null && _cachedSourcePath == normalizedPath)
            return _cachedApiIndex;

        using var activity = Telemetry.SdkChatTelemetry.StartExtraction("typescript", normalizedPath);
        try
        {
            var extractor = new TypeScriptApiExtractor();
            _cachedApiIndex = await extractor.ExtractAsync(normalizedPath, ct);
            _cachedSourcePath = normalizedPath;
            return _cachedApiIndex;
        }
        catch (Exception ex)
        {
            Telemetry.SdkChatTelemetry.RecordError(activity, ex);
            return null;
        }
    }
}
