using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentClientProtocol.Sdk;
using AgentClientProtocol.Sdk.Schema;
using Microsoft.Extensions.Logging;
using Sdk.Tools.Chat.Helpers;
using Sdk.Tools.Chat.Models;
using Sdk.Tools.Chat.Services.Languages;
using Sdk.Tools.Chat.Services.Languages.Samples;

namespace Sdk.Tools.Chat.Services;

/// <summary>
/// Orchestrates interactive sample generation with ACP protocol.
/// Uses AgentSideConnection to stream updates to the client.
/// </summary>
public class InteractiveSampleGenerator
{
    private readonly IAiService _aiService;
    private readonly FileHelper _fileHelper;
    private readonly ILogger<InteractiveSampleGenerator> _logger;
    
    public InteractiveSampleGenerator(
        IAiService aiService,
        FileHelper fileHelper,
        ILogger<InteractiveSampleGenerator> logger)
    {
        _aiService = aiService;
        _fileHelper = fileHelper;
        _logger = logger;
    }
    
    public async Task<string> GenerateAsync(
        string sessionId,
        string? workspacePath,
        ContentBlock[] prompt,
        AgentSideConnection connection,
        CancellationToken cancellationToken = default)
    {
        // Extract text from prompt
        var promptText = string.Join("\n", prompt
            .OfType<TextContent>()
            .Select(t => t.Text));
        
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return "Please provide a workspace path to generate samples for.";
        }
        
        // Auto-detect source, samples folders, and language
        _logger.LogInformation("Scanning SDK at {Path}", workspacePath);
        var sdkInfo = SdkInfo.Scan(workspacePath);
        if (sdkInfo.Language == null)
        {
            return $"Could not detect the language of the package at: {workspacePath}";
        }
        
        _logger.LogInformation("Detected {Language}, generating samples...", sdkInfo.LanguageName);
        
        // Create language context for streaming
        var context = CreateLanguageContext(sdkInfo.Language.Value);
        var systemPrompt = BuildSystemPrompt(context);
        
        // Stream user prompt (prefix + context) directly to AI without materialization
        var userPromptStream = StreamUserPromptAsync(promptText, sdkInfo.SourceFolder, context, cancellationToken);
        
        // Stream parsed samples as they complete
        List<GeneratedSample> samples = [];
        await foreach (var sample in _aiService.StreamItemsAsync<GeneratedSample>(
            systemPrompt, userPromptStream, null, null, cancellationToken))
        {
            if (!string.IsNullOrEmpty(sample.Name) && !string.IsNullOrEmpty(sample.Code))
            {
                samples.Add(sample);
            }
        }
        
        // Write to auto-detected output folder
        var outputFolder = sdkInfo.SuggestedSamplesFolder;
        Directory.CreateDirectory(outputFolder);
        
        // Write files
        foreach (var sample in samples)
        {
            // Use FilePath if provided, otherwise generate from Name
            var relativePath = !string.IsNullOrEmpty(sample.FilePath) 
                ? SanitizeFilePath(sample.FilePath, context.FileExtension)
                : SanitizeFileName(sample.Name) + context.FileExtension;
            var filePath = Path.Combine(outputFolder, relativePath);
            
            // Create subdirectories if needed
            var fileDir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(fileDir))
            {
                Directory.CreateDirectory(fileDir);
            }
            await File.WriteAllTextAsync(filePath, sample.Code, cancellationToken);
            _logger.LogInformation("Wrote {File}", filePath);
        }
        
        return $"Generated {samples.Count} sample(s) in {outputFolder}";
    }
    
    // Language context factory for all supported languages
    private SampleLanguageContext CreateLanguageContext(SdkLanguage language) => language switch
    {
        SdkLanguage.DotNet => new DotNetSampleLanguageContext(_fileHelper),
        SdkLanguage.Python => new PythonSampleLanguageContext(_fileHelper),
        SdkLanguage.JavaScript => new JavaScriptSampleLanguageContext(_fileHelper),
        SdkLanguage.TypeScript => new TypeScriptSampleLanguageContext(_fileHelper),
        SdkLanguage.Java => new JavaSampleLanguageContext(_fileHelper),
        SdkLanguage.Go => new GoSampleLanguageContext(_fileHelper),
        _ => throw new NotSupportedException($"Language {language} not supported")
    };
    
    private static string BuildSystemPrompt(SampleLanguageContext context) =>
        $"Generate runnable SDK samples. {context.GetInstructions()}";
    
    private static string BuildUserPromptPrefix(string? customPrompt)
    {
        var prompt = customPrompt ?? "Generate samples demonstrating the main features of this SDK.";
        return $"{prompt}\n\nSource code context:\n";
    }
    
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new System.Text.StringBuilder(name);
        foreach (var c in invalid) sanitized.Replace(c, '_');
        sanitized.Replace(':', '_');
        sanitized.Replace(' ', '_');
        return sanitized.ToString();
    }
    
    private static string SanitizeFilePath(string path, string expectedExtension)
    {
        var normalized = path.Replace('\\', '/');
        var parts = normalized.Split('/');
        var sanitizedParts = new List<string>();
        
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            var invalid = Path.GetInvalidFileNameChars();
            var sanitized = new System.Text.StringBuilder(part);
            foreach (var c in invalid) sanitized.Replace(c, '_');
            sanitized.Replace(':', '_');
            sanitized.Replace(' ', '_');
            sanitizedParts.Add(sanitized.ToString());
        }
        
        if (sanitizedParts.Count == 0) return "Sample" + expectedExtension;
        
        var result = string.Join(Path.DirectorySeparatorChar.ToString(), sanitizedParts);
        
        if (!result.EndsWith(expectedExtension, StringComparison.OrdinalIgnoreCase))
        {
            var lastPart = sanitizedParts[^1];
            var dotIndex = lastPart.LastIndexOf('.');
            if (dotIndex > 0)
            {
                sanitizedParts[^1] = lastPart[..dotIndex] + expectedExtension;
                result = string.Join(Path.DirectorySeparatorChar.ToString(), sanitizedParts);
            }
            else
            {
                result += expectedExtension;
            }
        }
        
        return result;
    }

    /// <summary>
    /// Streams the complete user prompt including prefix and context.
    /// </summary>
    private async IAsyncEnumerable<string> StreamUserPromptAsync(
        string? customPrompt,
        string sourceFolder,
        SampleLanguageContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Yield the prompt prefix
        yield return BuildUserPromptPrefix(customPrompt);
        
        // Stream context (source code)
        await foreach (var chunk in context.StreamContextAsync(
            new[] { sourceFolder }, null, ct: cancellationToken))
        {
            yield return chunk;
        }
    }
}
