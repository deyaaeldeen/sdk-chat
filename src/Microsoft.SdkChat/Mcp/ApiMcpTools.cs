// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Text.Json;
using Microsoft.SdkChat.Services;
using Microsoft.SdkChat.Telemetry;
using ModelContextProtocol.Server;

namespace Microsoft.SdkChat.Mcp;

/// <summary>
/// MCP tools for SDK API extraction and coverage analysis.
/// Entity group: api
/// </summary>
[McpServerToolType]
public class ApiMcpTools
{
    private readonly PackageInfoService _service = new();

    [McpServerTool(Name = "extract_api"), Description(
        "Extract the complete public API surface from an SDK package. " +
        "WHEN TO USE: To understand what operations an SDK provides, or to prepare context for code generation. " +
        "WHAT IT DOES: Parses all source files using language-specific analyzers (Roslyn for C#, ast for Python, etc.) and extracts public types, methods, properties with signatures and documentation. " +
        "RETURNS: Compact API representation - either human-readable code stubs or structured JSON. " +
        "TOKEN EFFICIENCY: ~70% smaller than raw source code while preserving all API information. " +
        "SUPPORTS: .NET/C#, Python, Java, JavaScript, TypeScript, Go.")]
    public async Task<string> ExtractApiAsync(
        [Description("Absolute path to SDK root directory.")] string packagePath,
        [Description("Force language instead of auto-detection. Values: dotnet, python, java, javascript, typescript, go.")] string? language = null,
        [Description("Output format: 'stubs' (default) for readable signatures, 'json' for structured data.")] string? format = null,
        [Description("Optional path to cross-language metadata JSON file.")] string? crossLanguageMetadata = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = SdkChatTelemetry.StartMcpTool("extract_api");

        try
        {
            var asJson = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
            var result = await _service.ExtractPublicApiAsync(packagePath, language, asJson, crossLanguageMetadata, cancellationToken).ConfigureAwait(false);

            if (!result.Success)
            {
                return McpToolResult.CreateFailure(
                    result.ErrorMessage ?? "API extraction failed",
                    result.ErrorCode ?? "EXTRACTION_FAILED"
                ).ToString();
            }

            return JsonSerializer.Serialize(
                new McpResponse<ApiExtractionResult> { Success = true, Data = result },
                McpJsonContext.Default.McpResponseApiExtractionResult);
        }
        catch (Exception ex)
        {
            SdkChatTelemetry.RecordError(activity, ex);
            return McpToolResult.CreateFailure($"Error extracting API: {ex.Message}", ex).ToString();
        }
    }

    [McpServerTool(Name = "analyze_coverage"), Description(
        "Analyze which SDK operations have sample coverage and which are missing. " +
        "WHEN TO USE: To identify documentation gaps and prioritize which samples to create. " +
        "WHAT IT DOES: Compares the public API surface against existing sample code to find which methods/operations are demonstrated and which are not. " +
        "RETURNS: Coverage percentage, list of covered operations (with file:line references), and uncovered operations (with signatures). " +
        "WORKFLOW: Run this → note uncovered operations → use build_samples_prompt with a prompt targeting the gaps.")]
    public async Task<string> AnalyzeCoverageAsync(
        [Description("Absolute path to SDK root directory.")] string packagePath,
        [Description("Path to samples/tests folder. If omitted, auto-detected from samples/, examples/, demo/, etc.")] string? samplesPath = null,
        [Description("Force language instead of auto-detection.")] string? language = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = SdkChatTelemetry.StartMcpTool("analyze_coverage");

        try
        {
            var result = await _service.AnalyzeCoverageAsync(packagePath, samplesPath, language, cancellationToken).ConfigureAwait(false);

            if (!result.Success)
            {
                return McpToolResult.CreateFailure(
                    result.ErrorMessage ?? "Coverage analysis failed",
                    result.ErrorCode ?? "ANALYSIS_FAILED"
                ).ToString();
            }

            return JsonSerializer.Serialize(
                new McpResponse<CoverageAnalysisResult> { Success = true, Data = result },
                McpJsonContext.Default.McpResponseCoverageAnalysisResult);
        }
        catch (Exception ex)
        {
            SdkChatTelemetry.RecordError(activity, ex);
            return McpToolResult.CreateFailure($"Error analyzing coverage: {ex.Message}", ex).ToString();
        }
    }
}
