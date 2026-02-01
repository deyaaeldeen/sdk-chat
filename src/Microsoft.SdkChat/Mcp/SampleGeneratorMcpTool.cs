using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ModelContextProtocol.Server;
using Microsoft.SdkChat.Helpers;
using Microsoft.SdkChat.Models;
using Microsoft.SdkChat.Services;
using Microsoft.SdkChat.Services.Languages;
using Microsoft.SdkChat.Services.Languages.Samples;
using Microsoft.SdkChat.Telemetry;

namespace Microsoft.SdkChat.Mcp;

/// <summary>
/// MCP tool wrapper for the sample generator.
/// </summary>
[McpServerToolType]
public class SampleGeneratorMcpTool(
    IAiService aiService,
    FileHelper fileHelper)
{
    
    [McpServerTool(Name = "generate_samples"), Description(
        "Generate production-quality SDK samples with 70% less AI token usage than raw source. " +
        "Uses semantic API extraction to understand operations and coverage analysis to avoid duplicates. " +
        "Supports .NET/C#, Python, Java, JavaScript, TypeScript, and Go SDKs. " +
        "Auto-detects project structure, locates source folders, and creates samples in the appropriate output directory. " +
        "Use this tool when you need documentation examples, quickstart code, or usage demonstrations for an SDK. " +
        "Returns JSON with success/errorCode for programmatic handling.")]
    public async Task<string> GenerateSamplesAsync(
        [Description("Absolute path to the SDK package root directory. The tool will automatically detect source folders (src/, lib/, etc.) and sample output locations (samples/, examples/, etc.).")] string packagePath,
        [Description("Optional output directory for generated samples. If not specified, the tool auto-detects the appropriate folder (e.g., 'samples', 'examples') or creates one.")] string? outputPath = null,
        [Description("Optional custom prompt to guide sample generation. Examples: 'Generate samples for authentication scenarios', 'Create examples showing error handling', 'Focus on async/await patterns'.")] string? prompt = null,
        [Description("Number of samples to generate. Default: 5.")] int? count = null,
        [Description("Max context size in characters. Controls how much source code is included in the prompt. Default: 512K (128K tokens).")] int? budget = null,
        [Description("AI model to use. Default depends on provider (gpt-4.1 for Copilot, gpt-4o for OpenAI).")] string? model = null,
        [Description("SDK language override. Auto-detected if not specified. Options: dotnet, python, java, javascript, typescript, go.")] string? language = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = SdkChatTelemetry.StartMcpTool("generate_samples");
        
        // Track token usage via events
        var promptTokens = 0;
        var responseTokens = 0;
        
        void OnPromptReady(object? sender, AiPromptReadyEventArgs e) => promptTokens = e.EstimatedTokens;
        void OnStreamComplete(object? sender, AiStreamCompleteEventArgs e) => responseTokens = e.EstimatedResponseTokens;
        
        aiService.PromptReady += OnPromptReady;
        aiService.StreamComplete += OnStreamComplete;
        
        try
        {
            // Auto-detect source, samples folders, and language (async to avoid blocking)
            var sdkInfo = await SdkInfo.ScanAsync(packagePath, cancellationToken).ConfigureAwait(false);
            
            // Use language override or auto-detected language
            SdkLanguage? effectiveLanguage = null;
            if (!string.IsNullOrEmpty(language))
            {
                effectiveLanguage = SdkLanguageHelpers.Parse(language);
                if (effectiveLanguage == SdkLanguage.Unknown)
                    effectiveLanguage = null;
            }
            effectiveLanguage ??= sdkInfo.Language;
            
            if (effectiveLanguage == null)
            {
                return McpToolResult.CreateFailure(
                    "Could not detect package language.",
                    "LANGUAGE_DETECTION_FAILED",
                    ["Ensure the path contains recognized SDK files", "Supported: .csproj, setup.py, pom.xml, package.json, go.mod", "Or specify language explicitly"]
                ).ToString();
            }
            
            activity?.SetTag("language", effectiveLanguage.Value.ToString());
            
            // Create language context for streaming
            var context = CreateLanguageContext(effectiveLanguage.Value);
            var systemPrompt = BuildSystemPrompt(context, count ?? 5);
            
            // Use specified budget or default
            var contextBudget = budget ?? SampleConstants.DefaultContextCharacters;
            var effectiveCount = count ?? 5;
            
            // Stream user prompt (prefix + context) directly to AI without materialization
            var userPromptStream = StreamUserPromptAsync(prompt, effectiveCount, sdkInfo.SourceFolder, context, contextBudget, cancellationToken);
            
            // Stream parsed samples as they complete
            List<GeneratedSample> samples = [];
            List<string> generatedFiles = [];
            await foreach (var sample in aiService.StreamItemsAsync<GeneratedSample>(
                systemPrompt, userPromptStream, model, null, cancellationToken))
            {
                if (!string.IsNullOrEmpty(sample.Name) && !string.IsNullOrEmpty(sample.Code))
                {
                    samples.Add(sample);
                }
            }
            
            // Write to auto-detected or specified output folder
            var output = Path.GetFullPath(outputPath ?? sdkInfo.SuggestedSamplesFolder);
            Directory.CreateDirectory(output);
            
            foreach (var sample in samples)
            {
                // Use FilePath if provided, otherwise generate from Name
                var relativePath = !string.IsNullOrEmpty(sample.FilePath) 
                    ? PathSanitizer.SanitizeFilePath(sample.FilePath, context.FileExtension)
                    : PathSanitizer.SanitizeFileName(sample.Name) + context.FileExtension;
                var filePath = Path.GetFullPath(Path.Combine(output, relativePath));

                // SECURITY: Ensure path stays within output directory (defense-in-depth)
                if (!filePath.StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) 
                    && !filePath.Equals(output, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // Skip files that would escape output directory
                }

                // Create subdirectories if needed
                var fileDir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(fileDir))
                {
                    Directory.CreateDirectory(fileDir);
                }
                await File.WriteAllTextAsync(filePath, sample.Code, cancellationToken).ConfigureAwait(false);
                generatedFiles.Add(filePath);
            }
            
            // Record telemetry with token counts
            SdkChatTelemetry.RecordSampleMetrics(activity, samples.Count, promptTokens, responseTokens);
            
            return McpToolResult.CreateSuccess(
                $"Generated {samples.Count} sample(s) in {output}",
                new ResultData
                {
                    Count = samples.Count,
                    OutputPath = output,
                    Files = [.. generatedFiles],
                    Language = effectiveLanguage.Value.ToString()
                }
            ).ToString();
        }
        catch (Exception ex)
        {
            SdkChatTelemetry.RecordError(activity, ex);
            return McpToolResult.CreateFailure($"Error generating samples: {ex.Message}", ex).ToString();
        }
        finally
        {
            aiService.PromptReady -= OnPromptReady;
            aiService.StreamComplete -= OnStreamComplete;
        }
    }
    
    // Language context factory for all supported languages
    private SampleLanguageContext CreateLanguageContext(SdkLanguage language) => language switch
    {
        SdkLanguage.DotNet => new DotNetSampleLanguageContext(fileHelper),
        SdkLanguage.Python => new PythonSampleLanguageContext(fileHelper),
        SdkLanguage.JavaScript => new JavaScriptSampleLanguageContext(fileHelper),
        SdkLanguage.TypeScript => new TypeScriptSampleLanguageContext(fileHelper),
        SdkLanguage.Java => new JavaSampleLanguageContext(fileHelper),
        SdkLanguage.Go => new GoSampleLanguageContext(fileHelper),
        _ => throw new NotSupportedException($"Language {language} not supported")
    };
    
    private static string BuildSystemPrompt(SampleLanguageContext context, int count) =>
        $"Generate {count} runnable SDK samples. {context.GetInstructions()}";
    
    private static string BuildUserPromptPrefix(string? customPrompt, int count)
    {
        var prompt = customPrompt ?? $"Generate {count} samples demonstrating the main features of this SDK.";
        return $"{prompt}\n\nSource code context:\n";
    }

    /// <summary>
    /// Streams the complete user prompt including prefix and context.
    /// Uses budget tracking to enforce limits.
    /// </summary>
    private static async IAsyncEnumerable<string> StreamUserPromptAsync(
        string? customPrompt,
        int count,
        string sourceFolder,
        SampleLanguageContext context,
        int contextBudget,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Create budget tracker - reserve space for prefix overhead
        const int OverheadReserve = 200;
        var budgetTracker = new Helpers.PromptBudgetTracker(contextBudget, OverheadReserve);
        
        // Yield the prompt prefix
        var prefix = BuildUserPromptPrefix(customPrompt, count);
        budgetTracker.TryConsume(prefix.Length);
        yield return prefix;
        
        // Stream context (source code) with remaining budget
        var remainingBudget = budgetTracker.Remaining;
        if (remainingBudget > 0)
        {
            await foreach (var chunk in context.StreamContextAsync(
                [sourceFolder], null, totalBudget: remainingBudget, ct: cancellationToken).ConfigureAwait(false))
            {
                yield return chunk;
            }
        }
    }
}
