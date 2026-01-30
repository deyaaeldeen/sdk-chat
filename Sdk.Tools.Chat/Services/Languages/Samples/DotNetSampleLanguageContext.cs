// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using ApiExtractor.Contracts;
using ApiExtractor.DotNet;
using Sdk.Tools.Chat.Helpers;
using Sdk.Tools.Chat.Models;

namespace Sdk.Tools.Chat.Services.Languages.Samples;

public sealed class DotNetSampleLanguageContext : SampleLanguageContext
{
    private readonly CSharpApiExtractor _extractor = new();
    private readonly CSharpUsageAnalyzer _usageAnalyzer = new();
    
    // Cached API index for reuse between StreamContextAsync and usage analysis
    private ApiIndex? _cachedApiIndex;
    private string? _cachedSourcePath;
    
    public DotNetSampleLanguageContext(FileHelper fileHelper) : base(fileHelper) { }

    public override SdkLanguage Language => SdkLanguage.DotNet;

    protected override string[] DefaultIncludeExtensions => [".cs"];

    protected override string[] DefaultExcludePatterns => 
    [ 
        "**/obj/**", 
        "**/bin/**", 
        "**/*.Designer.cs",
        "**/AssemblyInfo.cs"
    ];

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
        // Extract API surface (C# extractor is native - always available)
        var apiIndex = await GetOrExtractApiIndexAsync(sourcePath, ct);
        
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
            apiSurface = CSharpFormatter.FormatWithCoverage(apiIndex, coverage, maxLength);
        }
        else
        {
            // No coverage data - fall back to standard format
            apiSurface = CSharpFormatter.Format(apiIndex, maxLength);
        }
        
        // Yield unified API surface with coverage annotations
        yield return $"<api-surface package=\"{apiIndex.Package}\">\n";
        yield return apiSurface;
        yield return "</api-surface>\n";
    }
    
    /// <summary>
    /// Analyzes existing code (samples/tests) to extract API usage patterns.
    /// Returns structured coverage info instead of raw code - ~95% token reduction.
    /// </summary>
    public override async Task<UsageIndex?> AnalyzeUsageAsync(
        string sourcePath,
        string codePath,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(codePath))
            return null;
            
        // Get API index (may be cached from StreamContextAsync)
        var apiIndex = await GetOrExtractApiIndexAsync(sourcePath, ct);
        
        // Analyze usage
        return await _usageAnalyzer.AnalyzeAsync(codePath, apiIndex, ct);
    }
    
    /// <summary>
    /// Formats usage analysis as compact context for LLM.
    /// </summary>
    public override string FormatUsage(UsageIndex usage) => _usageAnalyzer.Format(usage);
    
    /// <summary>
    /// Gets cached API index or extracts it.
    /// </summary>
    private async Task<ApiIndex> GetOrExtractApiIndexAsync(string sourcePath, CancellationToken ct)
    {
        var normalizedPath = Path.GetFullPath(sourcePath);
        
        if (_cachedApiIndex != null && _cachedSourcePath == normalizedPath)
            return _cachedApiIndex;
            
        _cachedApiIndex = await _extractor.ExtractAsync(normalizedPath, ct);
        _cachedSourcePath = normalizedPath;
        
        return _cachedApiIndex;
    }

    protected override int GetPriority(FileMetadata file)
    {
        var path = file.RelativePath.Replace('\\', '/').ToLowerInvariant();
        var name = Path.GetFileNameWithoutExtension(file.FilePath).ToLowerInvariant();
        
        // Deprioritize generated code - load human-written code first
        var isGenerated = path.Contains("/generated/") || 
                          name.EndsWith(".g") ||
                          name.Contains("generated");
        var basePriority = isGenerated ? 100 : 0;
        
        // Within each category, prioritize key files
        if (name.Contains("client")) return basePriority + 1;
        if (name.Contains("options")) return basePriority + 2;
        if (name.Contains("model")) return basePriority + 3;
        return basePriority + 10;
    }

    public override string GetInstructions() =>
        "C#: file-scoped namespaces, var, async/await, using statements, try/catch, .NET 8+.";
}
