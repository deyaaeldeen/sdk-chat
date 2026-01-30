// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using Sdk.Tools.Chat.Models;

namespace Sdk.Tools.Chat.Services;

/// <summary>
/// Interface for AI service operations. Enables mocking for unit tests.
/// </summary>
public interface IAiService : IAsyncDisposable
{
    /// <summary>
    /// Whether using OpenAI-compatible API instead of Copilot.
    /// </summary>
    bool IsUsingOpenAi { get; }
    
    /// <summary>
    /// Raised when the prompt is ready to send (after materialization).
    /// </summary>
    event EventHandler<AiPromptReadyEventArgs>? PromptReady;
    
    /// <summary>
    /// Raised when streaming completes with usage statistics.
    /// </summary>
    event EventHandler<AiStreamCompleteEventArgs>? StreamComplete;

    /// <summary>
    /// Returns the effective model name that will be used for a request, after applying
    /// environment defaults and provider-specific configuration.
    /// </summary>
    string GetEffectiveModel(string? modelOverride = null);
    
    /// <summary>
    /// Stream AI response and yield parsed items as they complete.
    ///
    /// Contract: the underlying model output is expected to be NDJSON (newline-delimited JSON),
    /// where each line is a complete JSON object that can be deserialized into <typeparamref name="T"/>.
    /// </summary>
    IAsyncEnumerable<T> StreamItemsAsync<T>(
        string systemPrompt,
        IAsyncEnumerable<string> userPromptStream,
        string? model = null,
        ContextInfo? contextInfo = null,
        CancellationToken cancellationToken = default);
}
