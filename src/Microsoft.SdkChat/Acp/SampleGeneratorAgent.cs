// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AgentClientProtocol.Sdk;
using AgentClientProtocol.Sdk.Schema;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SdkChat.Helpers;
using Microsoft.SdkChat.Models;
using Microsoft.SdkChat.Services;
using Microsoft.SdkChat.Services.Languages.Samples;
using Microsoft.SdkChat.Telemetry;

namespace Microsoft.SdkChat.Acp;

/// <summary>
/// ACP agent implementation for interactive sample generation.
/// Uses the AgentClientProtocol.Sdk for protocol handling.
/// </summary>
public sealed class SampleGeneratorAgent(
    IServiceProvider services,
    ILogger<SampleGeneratorAgent> logger) : IAgent
{
    // Thread-safe session storage with cancellation support
    private readonly ConcurrentDictionary<string, AgentSessionState> _sessions = new();

    // Store connection per session for interactive generation (volatile for thread-safe reads)
    private volatile AgentSideConnection? _currentConnection;

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

        // Link the external cancellation token with session's cancellation token
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, sessionState.CancellationToken);
        var effectiveCt = linkedCts.Token;

        // Use workspace path from session's cwd
        var workspacePath = sessionState.WorkingDirectory ?? ".";

        // Stream status to client
        if (_currentConnection != null)
        {
            await _currentConnection.SendTextAsync(request.SessionId, $"Analyzing SDK at {workspacePath}...\n", effectiveCt).ConfigureAwait(false);
        }

        // Get services
        var aiService = services.GetRequiredService<IAiService>();
        var fileHelper = services.GetRequiredService<FileHelper>();

        // Track token usage via async callbacks
        var promptTokens = 0;
        var responseTokens = 0;

        ValueTask OnPromptReadyAsync(AiPromptReadyEventArgs e)
        {
            promptTokens = e.EstimatedTokens;
            return ValueTask.CompletedTask;
        }

        ValueTask OnStreamCompleteAsync(AiStreamCompleteEventArgs e)
        {
            responseTokens = e.EstimatedResponseTokens;
            return ValueTask.CompletedTask;
        }

        try
        {
            // Auto-detect source, samples folders, and language (async to avoid blocking)
            var sdkInfo = await SdkInfo.ScanAsync(workspacePath, effectiveCt).ConfigureAwait(false);
            if (sdkInfo.Language == null)
            {
                if (_currentConnection != null)
                {
                    await _currentConnection.SendTextAsync(request.SessionId, "Could not detect SDK language.\n", effectiveCt).ConfigureAwait(false);
                }
                return new PromptResponse { StopReason = StopReason.EndTurn };
            }

            activity?.SetTag("language", sdkInfo.LanguageName);

            if (_currentConnection != null)
            {
                await _currentConnection.SendTextAsync(request.SessionId, $"Detected {sdkInfo.LanguageName} SDK\n", effectiveCt).ConfigureAwait(false);
            }

            if (_currentConnection != null)
            {
                await _currentConnection.SendTextAsync(request.SessionId, $"Source: {sdkInfo.SourceFolder}\n", effectiveCt).ConfigureAwait(false);
                await _currentConnection.SendTextAsync(request.SessionId, $"Output: {sdkInfo.SuggestedSamplesFolder}\n\n", effectiveCt).ConfigureAwait(false);
                await _currentConnection.SendTextAsync(request.SessionId, "Generating samples...\n", effectiveCt).ConfigureAwait(false);
            }

            // Create appropriate language context
            var context = CreateLanguageContext(sdkInfo.Language.Value, fileHelper);

            // Use default budget for ACP (could be made configurable via request params)
            var contextBudget = SampleConstants.DefaultContextCharacters;

            var systemPrompt = $"Generate runnable SDK samples. {context.GetInstructions()}";
            var userPromptStream = StreamUserPromptAsync(sdkInfo.SourceFolder, context, contextBudget, effectiveCt);

            // Stream parsed samples as they complete
            List<GeneratedSample> samples = [];
            await foreach (var sample in aiService.StreamItemsAsync(
                systemPrompt, userPromptStream, AiStreamingJsonContext.CaseInsensitive.GeneratedSample, null, null,
                OnPromptReadyAsync, OnStreamCompleteAsync, effectiveCt))
            {
                if (!string.IsNullOrEmpty(sample.Name) && !string.IsNullOrEmpty(sample.Code))
                {
                    samples.Add(sample);
                }
            }

            var outputFolder = Path.GetFullPath(sdkInfo.SuggestedSamplesFolder);
            Directory.CreateDirectory(outputFolder);

            foreach (var sample in samples)
            {
                // Use FilePath if provided, otherwise generate from Name
                var relativePath = !string.IsNullOrEmpty(sample.FilePath)
                    ? PathSanitizer.SanitizeFilePath(sample.FilePath, context.FileExtension)
                    : PathSanitizer.SanitizeFileName(sample.Name) + context.FileExtension;
                var filePath = Path.GetFullPath(Path.Combine(outputFolder, relativePath));

                // SECURITY: Ensure path stays within output directory (defense-in-depth)
                if (!filePath.StartsWith(outputFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && !filePath.Equals(outputFolder, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // Skip files that would escape output directory
                }

                // Create subdirectories if needed
                var fileDir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(fileDir))
                {
                    Directory.CreateDirectory(fileDir);
                }

                await File.WriteAllTextAsync(filePath, sample.Code, effectiveCt).ConfigureAwait(false);

                if (_currentConnection != null)
                {
                    await _currentConnection.SendTextAsync(request.SessionId, $"✓ {filePath}\n", effectiveCt).ConfigureAwait(false);
                }
            }

            // Record telemetry with token counts
            SdkChatTelemetry.RecordSampleMetrics(activity, samples.Count, promptTokens, responseTokens);
            logger.LogInformation("Generated {Count} samples in {Path}", samples.Count, outputFolder);

            if (_currentConnection != null)
            {
                await _currentConnection.SendTextAsync(request.SessionId, $"\nDone! Generated {samples.Count} sample(s)\n", effectiveCt).ConfigureAwait(false);
            }

            return new PromptResponse { StopReason = StopReason.EndTurn };
        }
        finally
        {
            // Cleanup session to prevent memory leak
            CleanupSession(request.SessionId);
        }
    }

    /// <summary>
    /// Removes a session from storage and disposes its resources.
    /// Call this when a session completes or is cancelled.
    /// </summary>
    private void CleanupSession(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var state))
        {
            state.Dispose();
            logger.LogDebug("Cleaned up session {SessionId}", sessionId);
        }
    }

    private static SampleLanguageContext CreateLanguageContext(SdkLanguage language, FileHelper fileHelper) => language switch
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

        // Cancel the session's ongoing operations
        if (_sessions.TryGetValue(notification.SessionId, out var sessionState))
        {
            sessionState.RequestCancellation();
            logger.LogInformation("Cancelled session {SessionId}", notification.SessionId);
        }
        else
        {
            logger.LogWarning("Cancel requested for unknown session {SessionId}", notification.SessionId);
        }

        return Task.CompletedTask;
    }

    private static async IAsyncEnumerable<string> StreamUserPromptAsync(
        string sourceFolder,
        SampleLanguageContext context,
        int contextBudget,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Create budget tracker - reserve space for prefix overhead
        const int OverheadReserve = 100;
        var budgetTracker = new PromptBudgetTracker(contextBudget, OverheadReserve);

        var prefix = "Generate samples for this SDK:\n";
        budgetTracker.TryConsume(prefix.Length);
        yield return prefix;

        // Stream context with remaining budget
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

    /// <summary>Internal state for a session with cancellation support.</summary>
    private sealed class AgentSessionState : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();

        public required string SessionId { get; init; }
        public string? WorkingDirectory { get; init; }

        /// <summary>Token that will be cancelled when CancelAsync is called.</summary>
        public CancellationToken CancellationToken => _cts.Token;

        /// <summary>Request cancellation of this session's operations.</summary>
        public void RequestCancellation() => _cts.Cancel();

        public void Dispose() => _cts.Dispose();
    }
}
