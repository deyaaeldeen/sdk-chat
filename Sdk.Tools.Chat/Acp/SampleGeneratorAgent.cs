using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentClientProtocol.Sdk;
using AgentClientProtocol.Sdk.Schema;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sdk.Tools.Chat.Helpers;
using Sdk.Tools.Chat.Models;
using Sdk.Tools.Chat.Services;
using Sdk.Tools.Chat.Services.Languages;
using Sdk.Tools.Chat.Services.Languages.Samples;
using Sdk.Tools.Chat.Telemetry;

namespace Sdk.Tools.Chat.Acp;

/// <summary>
/// ACP agent implementation for interactive sample generation.
/// Uses the AgentClientProtocol.Sdk for protocol handling.
/// </summary>
public sealed class SampleGeneratorAgent(
    IServiceProvider services, 
    ILogger<SampleGeneratorAgent> logger) : IAgent
{
    // Thread-safe session storage
    private readonly ConcurrentDictionary<string, AgentSessionState> _sessions = new();
    
    // Store connection per session for interactive generation
    private AgentSideConnection? _currentConnection;
    
    public void SetConnection(AgentSideConnection connection)
    {
        _currentConnection = connection;
    }
    
    public Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken ct = default)
    {
        return Task.FromResult(new InitializeResponse
        {
            ProtocolVersion = Protocol.Version,
            AgentCapabilities = new AgentCapabilities
            {
                SessionCapabilities = new SessionCapabilities()
            },
            AgentInfo = new Implementation
            {
                Name = "sdk-chat",
                Version = "1.0.0",
                Title = "SDK Chat Sample Generator"
            }
        });
    }
    
    public Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken ct = default)
    {
        var sessionId = $"sess_{Guid.NewGuid():N}";
        
        var state = new AgentSessionState
        {
            SessionId = sessionId,
            WorkingDirectory = request.Cwd
        };
        
        if (!_sessions.TryAdd(sessionId, state))
        {
            throw new InvalidOperationException($"Session {sessionId} already exists");
        }
        
        logger.LogDebug("Created session {SessionId} with cwd {Cwd}", sessionId, request.Cwd);
        
        return Task.FromResult(new NewSessionResponse
        {
            SessionId = sessionId
        });
    }
    
    public async Task<PromptResponse> PromptAsync(PromptRequest request, CancellationToken ct = default)
    {
        using var activity = SdkChatTelemetry.StartAcpSession(request.SessionId, "prompt");
        
        if (!_sessions.TryGetValue(request.SessionId, out var sessionState))
        {
            throw new InvalidOperationException($"Unknown session: {request.SessionId}");
        }
        
        // Use workspace path from session's cwd
        var workspacePath = sessionState.WorkingDirectory ?? ".";
        
        // Stream status to client
        if (_currentConnection != null)
        {
            await _currentConnection.SendTextAsync(request.SessionId, $"Analyzing SDK at {workspacePath}...\n", ct).ConfigureAwait(false);
        }
        
        // Get services
        var aiService = services.GetRequiredService<IAiService>();
        var fileHelper = services.GetRequiredService<FileHelper>();
        
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
            var sdkInfo = await SdkInfo.ScanAsync(workspacePath, ct).ConfigureAwait(false);
        if (sdkInfo.Language == null)
        {
            if (_currentConnection != null)
            {
                await _currentConnection.SendTextAsync(request.SessionId, "Could not detect SDK language.\n", ct).ConfigureAwait(false);
            }
            return new PromptResponse { StopReason = StopReason.EndTurn };
        }
        
        activity?.SetTag("language", sdkInfo.LanguageName);
        
        if (_currentConnection != null)
        {
            await _currentConnection.SendTextAsync(request.SessionId, $"Detected {sdkInfo.LanguageName} SDK\n", ct).ConfigureAwait(false);
        }
        
        if (_currentConnection != null)
        {
            await _currentConnection.SendTextAsync(request.SessionId, $"Source: {sdkInfo.SourceFolder}\n", ct).ConfigureAwait(false);
            await _currentConnection.SendTextAsync(request.SessionId, $"Output: {sdkInfo.SuggestedSamplesFolder}\n\n", ct).ConfigureAwait(false);
            await _currentConnection.SendTextAsync(request.SessionId, "Generating samples...\n", ct).ConfigureAwait(false);
        }
        
        // Create appropriate language context
        var context = CreateLanguageContext(sdkInfo.Language.Value, fileHelper);
        
        var systemPrompt = $"Generate runnable SDK samples. {context.GetInstructions()}";
        var userPromptStream = StreamUserPromptAsync(sdkInfo.SourceFolder, context, ct);
        
        // Stream parsed samples as they complete
        List<GeneratedSample> samples = [];
        await foreach (var sample in aiService.StreamItemsAsync<GeneratedSample>(
            systemPrompt, userPromptStream, null, null, ct))
        {
            if (!string.IsNullOrEmpty(sample.Name) && !string.IsNullOrEmpty(sample.Code))
            {
                samples.Add(sample);
            }
        }
        
        var outputFolder = sdkInfo.SuggestedSamplesFolder;
        Directory.CreateDirectory(outputFolder);
        
        foreach (var sample in samples)
        {
            // Use FilePath if provided, otherwise generate from Name
            var relativePath = !string.IsNullOrEmpty(sample.FilePath) 
                ? PathSanitizer.SanitizeFilePath(sample.FilePath, context.FileExtension)
                : PathSanitizer.SanitizeFileName(sample.Name) + context.FileExtension;
            var filePath = Path.Combine(outputFolder, relativePath);
            
            // Create subdirectories if needed
            var fileDir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(fileDir))
            {
                Directory.CreateDirectory(fileDir);
            }
            
            await File.WriteAllTextAsync(filePath, sample.Code, ct).ConfigureAwait(false);
            
            if (_currentConnection != null)
            {
                await _currentConnection.SendTextAsync(request.SessionId, $"✓ {relativePath}\n", ct).ConfigureAwait(false);
            }
        }
        
        // Record telemetry with token counts
        SdkChatTelemetry.RecordSampleMetrics(activity, samples.Count, promptTokens, responseTokens);
        logger.LogInformation("Generated {Count} samples in {Path}", samples.Count, outputFolder);
        
        if (_currentConnection != null)
        {
            await _currentConnection.SendTextAsync(request.SessionId, $"\nDone! Generated {samples.Count} sample(s)\n", ct).ConfigureAwait(false);
        }
        
        return new PromptResponse { StopReason = StopReason.EndTurn };
        }
        finally
        {
            aiService.PromptReady -= OnPromptReady;
            aiService.StreamComplete -= OnStreamComplete;
        }
    }
    
    private SampleLanguageContext CreateLanguageContext(SdkLanguage language, FileHelper fileHelper) => language switch
    {
        SdkLanguage.DotNet => new DotNetSampleLanguageContext(fileHelper),
        SdkLanguage.Python => new PythonSampleLanguageContext(fileHelper),
        SdkLanguage.JavaScript => new JavaScriptSampleLanguageContext(fileHelper),
        SdkLanguage.TypeScript => new TypeScriptSampleLanguageContext(fileHelper),
        SdkLanguage.Java => new JavaSampleLanguageContext(fileHelper),
        SdkLanguage.Go => new GoSampleLanguageContext(fileHelper),
        _ => new DotNetSampleLanguageContext(fileHelper) // fallback
    };
    
    public Task CancelAsync(CancelNotification notification, CancellationToken ct = default)
    {
        logger.LogDebug("Cancel requested for session {SessionId}", notification.SessionId);
        return Task.CompletedTask;
    }
    
    private static async IAsyncEnumerable<string> StreamUserPromptAsync(
        string sourceFolder,
        SampleLanguageContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return "Generate samples for this SDK:\n";
        
        await foreach (var chunk in context.StreamContextAsync(
            [sourceFolder], null, ct: cancellationToken).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }
    
    /// <summary>Internal state for a session.</summary>
    private sealed class AgentSessionState
    {
        public required string SessionId { get; init; }
        public string? WorkingDirectory { get; init; }
    }
}
