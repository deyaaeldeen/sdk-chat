// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Text.Json;
using Microsoft.SdkChat.Helpers;
using Microsoft.SdkChat.Models;
using Microsoft.SdkChat.Services;
using Microsoft.SdkChat.Telemetry;
using ModelContextProtocol.Server;

namespace Microsoft.SdkChat.Mcp;

/// <summary>
/// MCP tools for SDK sample detection and generation.
/// Entity group: samples
/// </summary>
[McpServerToolType]
public class SamplesMcpTools(
    IAiService aiService,
    FileHelper fileHelper)
{
    private readonly PackageInfoService _infoService = new();
    private readonly SampleGeneratorService _generatorService = new(aiService, fileHelper);

    [McpServerTool(Name = "detect_samples"), Description(
        "Find existing samples/examples folder in an SDK package. " +
        "WHEN TO USE: Before generating samples to check what already exists and avoid duplicates. " +
        "WHAT IT DOES: Searches for common sample directories (samples/, examples/, demo/, quickstarts/) and counts existing files. " +
        "RETURNS: Found samples path, suggested output path, all candidate folders, and whether samples already exist. " +
        "NEXT STEPS: Use analyze_coverage to see which APIs are already covered, then generate_samples for the gaps.")]
    public async Task<string> DetectSamplesAsync(
        [Description("Absolute path to SDK root directory.")] string packagePath,
        CancellationToken cancellationToken = default)
    {
        using var activity = SdkChatTelemetry.StartMcpTool("detect_samples");

        try
        {
            var result = await _infoService.DetectSamplesFolderAsync(packagePath, cancellationToken).ConfigureAwait(false);

            return JsonSerializer.Serialize(
                new McpResponse<SamplesFolderResult> { Success = true, Data = result },
                McpJsonContext.Default.McpResponseSamplesFolderResult);
        }
        catch (Exception ex)
        {
            SdkChatTelemetry.RecordError(activity, ex);
            return McpToolResult.CreateFailure($"Error detecting samples: {ex.Message}", ex).ToString();
        }
    }

    [McpServerTool(Name = "generate_samples"), Description(
        "Generate production-ready SDK code samples using AI. " +
        "WHEN TO USE: To create documentation examples, quickstart code, or usage demonstrations for an SDK. " +
        "WHAT IT DOES: Extracts the public API, analyzes existing samples to avoid duplicates, then generates idiomatic code samples with proper patterns. " +
        "TOKEN EFFICIENCY: Uses ~70% less tokens than raw source by extracting semantic API information. " +
        "RETURNS: JSON with generated file paths, count, and language. Files are written to the output directory. " +
        "SUPPORTS: .NET/C#, Python, Java, JavaScript, TypeScript, Go. " +
        "WORKFLOW: detect_source → analyze_coverage → generate_samples with prompt targeting uncovered APIs.")]
    public async Task<string> GenerateSamplesAsync(
        [Description("Absolute path to SDK root. Must contain project files (.csproj, pyproject.toml, pom.xml, package.json, go.mod).")] string packagePath,
        [Description("Where to write samples. Default: auto-detected samples/, examples/, or new 'examples' folder.")] string? outputPath = null,
        [Description("Guide the AI: 'streaming examples', 'error handling patterns', 'authentication scenarios', 'async/await usage'.")] string? prompt = null,
        [Description("How many samples to generate. Default: 5.")] int? count = null,
        [Description("Max source context in characters. Default: 512K (128K tokens). Reduce for faster/cheaper generation.")] int? budget = null,
        [Description("AI model override. Default: gpt-4.1 (Copilot) or gpt-4o (OpenAI).")] string? model = null,
        [Description("Force language: dotnet, python, java, javascript, typescript, go. Default: auto-detected.")] string? language = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = SdkChatTelemetry.StartMcpTool("generate_samples");

        try
        {
            var options = new SampleGenerationOptions
            {
                PackagePath = packagePath,
                OutputPath = outputPath,
                Prompt = prompt,
                Count = count ?? 5,
                Budget = budget ?? SampleConstants.DefaultContextCharacters,
                Model = model,
                Language = language
            };

            var result = await _generatorService.GenerateAsync(options, progress: null, cancellationToken).ConfigureAwait(false);

            if (!result.Success)
            {
                return McpToolResult.CreateFailure(
                    result.ErrorMessage ?? "Sample generation failed",
                    result.ErrorCode ?? "GENERATION_FAILED"
                ).ToString();
            }

            activity?.SetTag("language", result.Language);
            SdkChatTelemetry.RecordSampleMetrics(activity, result.Count, result.PromptTokens, result.ResponseTokens);

            return McpToolResult.CreateSuccess(
                $"Generated {result.Count} sample(s) in {result.OutputPath}",
                new ResultData
                {
                    Count = result.Count,
                    OutputPath = result.OutputPath,
                    Files = result.GeneratedFiles,
                    Language = result.Language
                }
            ).ToString();
        }
        catch (Exception ex)
        {
            SdkChatTelemetry.RecordError(activity, ex);
            return McpToolResult.CreateFailure($"Error generating samples: {ex.Message}", ex).ToString();
        }
    }
}
