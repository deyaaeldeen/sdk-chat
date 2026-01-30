using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ModelContextProtocol.Server;
using Sdk.Tools.Chat.Helpers;
using Sdk.Tools.Chat.Models;
using Sdk.Tools.Chat.Services;
using Sdk.Tools.Chat.Services.Languages;
using Sdk.Tools.Chat.Services.Languages.Samples;
using Sdk.Tools.Chat.Telemetry;

namespace Sdk.Tools.Chat.Mcp;

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
            if (sdkInfo.Language == null)
            {
                return McpToolResult.CreateFailure(
                    "Could not detect package language.",
                    "LANGUAGE_DETECTION_FAILED",
                    ["Ensure the path contains recognized SDK files", "Supported: .csproj, setup.py, pom.xml, package.json, go.mod"]
                ).ToString();
            }
            
            activity?.SetTag("language", sdkInfo.LanguageName);
            
            // Create language context for streaming
            var context = CreateLanguageContext(sdkInfo.Language.Value);
            var systemPrompt = BuildSystemPrompt(context);
            
            // Stream user prompt (prefix + context) directly to AI without materialization
            var userPromptStream = StreamUserPromptAsync(prompt, sdkInfo.SourceFolder, context, cancellationToken);
            
            // Stream parsed samples as they complete
            List<GeneratedSample> samples = [];
            List<string> generatedFiles = [];
            await foreach (var sample in aiService.StreamItemsAsync<GeneratedSample>(
                systemPrompt, userPromptStream, null, null, cancellationToken))
            {
                if (!string.IsNullOrEmpty(sample.Name) && !string.IsNullOrEmpty(sample.Code))
                {
                    samples.Add(sample);
                }
            }
            
            // Write to auto-detected or specified output folder
            var output = outputPath ?? sdkInfo.SuggestedSamplesFolder;
            Directory.CreateDirectory(output);
            
            foreach (var sample in samples)
            {
                // Use FilePath if provided, otherwise generate from Name
                var relativePath = !string.IsNullOrEmpty(sample.FilePath) 
                    ? PathSanitizer.SanitizeFilePath(sample.FilePath, context.FileExtension)
                    : PathSanitizer.SanitizeFileName(sample.Name) + context.FileExtension;
                var filePath = Path.Combine(output, relativePath);
                
                // Create subdirectories if needed
                var fileDir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(fileDir))
                {
                    Directory.CreateDirectory(fileDir);
                }
                await File.WriteAllTextAsync(filePath, sample.Code, cancellationToken).ConfigureAwait(false);
                generatedFiles.Add(relativePath);
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
                    Language = sdkInfo.LanguageName
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
    
    private static string BuildSystemPrompt(SampleLanguageContext context) =>
        $"Generate runnable SDK samples. {context.GetInstructions()}";
    
    private static string BuildUserPromptPrefix(string? customPrompt)
    {
        var prompt = customPrompt ?? "Generate samples demonstrating the main features of this SDK.";
        return $"{prompt}\n\nSource code context:\n";
    }

    /// <summary>
    /// Streams the complete user prompt including prefix and context.
    /// </summary>
    private static async IAsyncEnumerable<string> StreamUserPromptAsync(
        string? customPrompt,
        string sourceFolder,
        SampleLanguageContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Yield the prompt prefix
        yield return BuildUserPromptPrefix(customPrompt);
        
        // Stream context (source code)
        await foreach (var chunk in context.StreamContextAsync(
            [sourceFolder], null, ct: cancellationToken).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }
}
