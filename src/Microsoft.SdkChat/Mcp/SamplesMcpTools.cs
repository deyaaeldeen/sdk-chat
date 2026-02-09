// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Text.Json;
using Microsoft.SdkChat.Helpers;
using Microsoft.SdkChat.Models;
using Microsoft.SdkChat.Services;
using Microsoft.SdkChat.Services.Languages.Samples;
using Microsoft.SdkChat.Telemetry;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Microsoft.SdkChat.Mcp;

/// <summary>
/// MCP tools for SDK sample detection and generation.
/// Entity group: samples
/// </summary>
[McpServerToolType]
public class SamplesMcpTools(IMcpSampler mcpSampler, FileHelper fileHelper, IPackageInfoService packageInfoService)
{
    private readonly IPackageInfoService _infoService = packageInfoService;
    private readonly FileHelper _fileHelper = fileHelper;
    private readonly IMcpSampler _mcpSampler = mcpSampler;

    [McpServerTool(Name = "detect_samples"), Description(
        "Find existing samples/examples folder in an SDK package. " +
        "WHEN TO USE: Before generating samples to check what already exists and avoid duplicates. " +
        "WHAT IT DOES: Searches for common sample directories (samples/, examples/, demo/, quickstarts/) and counts existing files. " +
        "RETURNS: Found samples path, suggested output path, all candidate folders, and whether samples already exist. " +
        "NEXT STEPS: Use analyze_coverage to see which APIs are already covered, then generate_samples with prompt targeting uncovered APIs.")]
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
        "Generate production-ready SDK code samples using AI via MCP Sampling. " +
        "WHEN TO USE: To create documentation examples, quickstart code, or usage demonstrations for an SDK. " +
        "WHAT IT DOES: Extracts the public API, analyzes existing samples to avoid duplicates, builds a prompt, then requests the host LLM to generate idiomatic code samples. " +
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
        [Description("Max tokens for the LLM response. Default: 16000. Increase for more/larger samples.")] int? maxTokens = null,
        [Description("Force language: dotnet, python, java, javascript, typescript, go. Default: auto-detected.")] string? language = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = SdkChatTelemetry.StartMcpTool("generate_samples");

        try
        {
            var sampleCount = count ?? 5;
            var contextBudget = budget ?? SampleConstants.DefaultContextCharacters;

            // Detect SDK language and structure
            var packageFullPath = Path.GetFullPath(packagePath);
            if (!Directory.Exists(packageFullPath))
            {
                return McpToolResult.CreateFailure(
                    $"SDK path does not exist: {packageFullPath}",
                    "PATH_NOT_FOUND"
                ).ToString();
            }

            var sourceResult = await _infoService.DetectSourceFolderAsync(packageFullPath, language, cancellationToken).ConfigureAwait(false);
            var samplesResult = await _infoService.DetectSamplesFolderAsync(packageFullPath, cancellationToken).ConfigureAwait(false);

            // Determine language
            SdkLanguage? effectiveLanguage = null;
            if (!string.IsNullOrEmpty(sourceResult.Language))
            {
                effectiveLanguage = SdkLanguageHelpers.Parse(sourceResult.Language);
                if (effectiveLanguage == SdkLanguage.Unknown)
                    effectiveLanguage = null;
            }

            if (effectiveLanguage == null)
            {
                return McpToolResult.CreateFailure(
                    "Could not detect package language.",
                    "LANGUAGE_DETECTION_FAILED"
                ).ToString();
            }

            // Create language context
            var context = SampleGeneratorService.CreateLanguageContextForLanguage(effectiveLanguage.Value, _fileHelper);
            var existingSampleCount = samplesResult.SamplesFolder is not null && Directory.Exists(samplesResult.SamplesFolder)
                ? SdkInfo.CountFilesSafely(samplesResult.SamplesFolder, $"*{context.FileExtension}")
                : 0;

            // Determine output path
            var outputFullPath = Path.GetFullPath(outputPath ?? samplesResult.SuggestedSamplesFolder);

            // Build prompts
            var sdkName = sourceResult.SdkName ?? Path.GetFileName(packageFullPath);
            var baseSystemPrompt = SampleGeneratorService.BuildSystemPrompt(context, sdkName, sampleCount);

            // Append JSON schema instructions (shared with CLI and ACP)
            var systemPrompt = $"{baseSystemPrompt}\n\n{SampleResponseParser.GetJsonArrayFormatInstructions()}";

            // Build user prompt by streaming context
            var userPrompt = await BuildUserPromptAsync(
                prompt,
                sampleCount,
                existingSampleCount > 0,
                sourceResult.SourceFolder ?? packageFullPath,
                samplesResult.SamplesFolder,
                outputFullPath,
                context,
                contextBudget,
                cancellationToken).ConfigureAwait(false);

            // Request samples from host LLM via MCP Sampling
            // Let the host control model parameters (temperature, etc.), but set reasonable token limit for code generation
            var createMessageRequest = new CreateMessageRequestParams
            {
                Messages =
                [
                    new SamplingMessage
                    {
                        Role = Role.User,
                        Content = [new TextContentBlock { Text = userPrompt }]
                    }
                ],
                SystemPrompt = systemPrompt,
                MaxTokens = maxTokens ?? 16000, // Reasonable default for code generation; host can override
                IncludeContext = ContextInclusion.ThisServer
            };

            // Request samples with retry on parse failure
            const int MaxAttempts = SampleResponseParser.MaxRetryAttempts;
            List<GeneratedSample>? samples = null;
            string? lastResponseText = null;

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                var samplingResult = await _mcpSampler.SampleAsync(createMessageRequest, cancellationToken: cancellationToken).ConfigureAwait(false);

                lastResponseText = ExtractTextFromSamplingResponse(samplingResult);
                if (string.IsNullOrWhiteSpace(lastResponseText))
                    continue;

                try
                {
                    samples = SampleResponseParser.ParseJsonArray(lastResponseText);
                    break; // Parsed successfully
                }
                catch (JsonException) when (attempt < MaxAttempts)
                {
                    // Retry: replace user message with a correction prompt
                    createMessageRequest.Messages =
                    [
                        new SamplingMessage
                        {
                            Role = Role.User,
                            Content = [new TextContentBlock { Text = userPrompt }]
                        },
                        new SamplingMessage
                        {
                            Role = Role.Assistant,
                            Content = [new TextContentBlock { Text = lastResponseText }]
                        },
                        new SamplingMessage
                        {
                            Role = Role.User,
                            Content = [new TextContentBlock { Text = SampleResponseParser.CorrectionPrompt }]
                        }
                    ];
                }
                catch (JsonException)
                {
                    // Final attempt failed
                }
            }

            if (samples is null)
            {
                var errorCode = string.IsNullOrWhiteSpace(lastResponseText) ? "EMPTY_RESPONSE" : "PARSE_ERROR";
                var errorMessage = string.IsNullOrWhiteSpace(lastResponseText)
                    ? "No content in LLM response"
                    : "Could not parse samples from LLM response after retry. Expected JSON array of samples.";
                return McpToolResult.CreateFailure(errorMessage, errorCode).ToString();
            }

            // Write samples to disk
            var writtenFiles = new List<string>();
            Directory.CreateDirectory(outputFullPath);

            // Normalize output path for containment checks
            var normalizedOutputPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputFullPath));

            // Filter out samples with missing name or code explicitly
            var validSamples = samples.Where(s => !string.IsNullOrEmpty(s.Name) && !string.IsNullOrEmpty(s.Code));

            foreach (var sample in validSamples)
            {
                var relativePath = !string.IsNullOrEmpty(sample.FilePath)
                    ? PathSanitizer.SanitizeFilePath(sample.FilePath, context.FileExtension)
                    : PathSanitizer.SanitizeFileName(sample.Name) + context.FileExtension;

                var filePath = Path.GetFullPath(Path.Combine(outputFullPath, relativePath));

                // Security: ensure path stays within output directory
                var normalizedFilePath = Path.GetFullPath(filePath);
                if (!normalizedFilePath.StartsWith(normalizedOutputPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && !normalizedFilePath.Equals(normalizedOutputPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var fileDir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(fileDir))
                    Directory.CreateDirectory(fileDir);

                await File.WriteAllTextAsync(filePath, sample.Code, cancellationToken).ConfigureAwait(false);
                writtenFiles.Add(filePath);
            }

            activity?.SetTag("language", effectiveLanguage.Value.ToString());
            // Estimate token usage from prompt/response lengths (MCP sampling doesn't expose exact counts)
            var estimatedPromptTokens = (systemPrompt.Length + userPrompt.Length) / 4;
            var estimatedResponseTokens = (lastResponseText?.Length ?? 0) / 4;
            SdkChatTelemetry.RecordSampleMetrics(activity, writtenFiles.Count, estimatedPromptTokens, estimatedResponseTokens);

            return McpToolResult.CreateSuccess(
                $"Generated {writtenFiles.Count} sample(s) in {outputFullPath}",
                new ResultData
                {
                    Count = writtenFiles.Count,
                    OutputPath = outputFullPath,
                    Files = [.. writtenFiles],
                    Language = effectiveLanguage.Value.ToString()
                }
            ).ToString();
        }
        catch (Exception ex)
        {
            SdkChatTelemetry.RecordError(activity, ex);
            return McpToolResult.CreateFailure($"Error generating samples: {ex.Message}", ex).ToString();
        }
    }

    private static async Task<string> BuildUserPromptAsync(
        string? customPrompt,
        int count,
        bool hasExistingSamples,
        string sourceFolder,
        string? samplesFolder,
        string outputFolder,
        SampleLanguageContext context,
        int budget,
        CancellationToken cancellationToken)
    {
        const int OverheadReserve = 500;
        var budgetTracker = new PromptBudgetTracker(budget, OverheadReserve);
        var parts = new List<string>();

        // Prefix
        var prefix = SampleGeneratorService.GetUserPromptPrefix(customPrompt, count, hasExistingSamples);
        budgetTracker.TryConsume(prefix.Length);
        parts.Add(prefix);

        // Output folder context
        var outputFolderTag = $"<output-folder>{Path.GetFileName(outputFolder)}</output-folder>\n";
        var outputInstruction = "Generate filePath relative to the output folder above.\n\n";
        budgetTracker.TryConsume(outputFolderTag.Length + outputInstruction.Length);
        parts.Add(outputFolderTag);
        parts.Add(outputInstruction);

        // Include example sample if available
        if (!string.IsNullOrEmpty(samplesFolder) && Directory.Exists(samplesFolder) && !budgetTracker.IsExhausted)
        {
            var exampleFile = SdkInfo.EnumerateFilesSafely(samplesFolder, $"*{context.FileExtension}", maxFiles: 100)
                .FirstOrDefault();
            if (exampleFile != null)
            {
                var relativePath = Path.GetRelativePath(samplesFolder, exampleFile);
                var content = await File.ReadAllTextAsync(exampleFile, cancellationToken);

                var exampleBudget = Math.Min(5000, budgetTracker.Remaining / 2);
                if (exampleBudget > 500)
                {
                    var maxContentLength = exampleBudget - 200;
                    if (content.Length > maxContentLength)
                    {
                        content = content[..maxContentLength] + "\n// ... (truncated)";
                    }

                    var exampleXml = $"<example-sample>\n<filePath>{relativePath}</filePath>\n<code>\n{content}\n</code>\n</example-sample>\n";
                    var instruction = "Follow this exact structure and style for new samples.\n\n";

                    budgetTracker.TryConsume(exampleXml.Length + instruction.Length);
                    parts.Add(exampleXml);
                    parts.Add(instruction);
                }
            }
        }

        // Stream source context
        if (budgetTracker.Remaining > 0)
        {
            await foreach (var chunk in context.StreamContextAsync(
                sourcePath: sourceFolder,
                samplesPath: samplesFolder,
                config: null,
                totalBudget: budgetTracker.Remaining,
                ct: cancellationToken))
            {
                parts.Add(chunk);
            }
        }

        return string.Concat(parts);
    }

    private static string ExtractTextFromSamplingResponse(CreateMessageResult result)
    {
        var textBlocks = result.Content.OfType<TextContentBlock>();
        return string.Concat(textBlocks.Select(b => b.Text ?? string.Empty));
    }

}
