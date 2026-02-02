// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.SdkChat.Configuration;

namespace Microsoft.SdkChat.Services;

/// <summary>
/// Scrubs sensitive data (API keys, tokens, secrets) from text before logging.
/// Defense-in-depth: prevents accidental credential exposure in debug logs.
/// </summary>
public static partial class SensitiveDataScrubber
{
    // Compiled regex patterns for common secret formats
    // Using source generators for performance (Regex attribute)

    /// <summary>OpenAI API keys: sk-... or sk-proj-...</summary>
    [GeneratedRegex(@"sk-(?:proj-)?[a-zA-Z0-9]{20,}", RegexOptions.Compiled)]
    private static partial Regex OpenAiKeyPattern();

    /// <summary>GitHub tokens: ghp_, gho_, ghu_, ghs_, ghr_</summary>
    [GeneratedRegex(@"gh[pousr]_[a-zA-Z0-9]{36,}", RegexOptions.Compiled)]
    private static partial Regex GitHubTokenPattern();

    /// <summary>Generic Bearer tokens</summary>
    [GeneratedRegex(@"Bearer\s+[a-zA-Z0-9\-_\.]+", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex BearerTokenPattern();

    /// <summary>Generic API key patterns in key=value or "key": "value" format</summary>
    [GeneratedRegex(@"(?i)(api[_-]?key|apikey|secret|password|token|credential|auth)([""']?\s*[:=]\s*[""']?)([a-zA-Z0-9\-_\.]{16,})", RegexOptions.Compiled)]
    private static partial Regex GenericSecretPattern();

    /// <summary>Long hex strings that look like API keys (32+ chars, preceded by key-like word)</summary>
    [GeneratedRegex(@"(?i)(?:key|secret|password|token)([""']?\s*[:=]\s*[""']?)([a-fA-F0-9]{32,})", RegexOptions.Compiled)]
    private static partial Regex HexKeyPattern();

    private const string RedactedPlaceholder = "[REDACTED]";

    /// <summary>
    /// Environment variable name patterns that may contain secrets.
    /// Any env var matching these patterns will have its value scrubbed.
    /// </summary>
    private static readonly string[] SensitiveEnvVarPatterns =
    [
        "_KEY",
        "_TOKEN",
        "_SECRET",
        "_PASSWORD",
        "_CREDENTIAL"
    ];

    // Lazy-loaded cache of actual secret values from environment (for runtime scrubbing)
    private static readonly Lazy<HashSet<string>> _cachedSecretValues = new(LoadSecretValuesFromEnvironment);

    private static HashSet<string> LoadSecretValuesFromEnvironment()
    {
        var secrets = new HashSet<string>(StringComparer.Ordinal);

        // Scan all environment variables for sensitive patterns
        foreach (var key in Environment.GetEnvironmentVariables().Keys.Cast<string>())
        {
            var isSensitive = SensitiveEnvVarPatterns.Any(pattern =>
                key.Contains(pattern, StringComparison.OrdinalIgnoreCase));

            if (isSensitive)
            {
                var value = Environment.GetEnvironmentVariable(key);
                // Only cache non-trivial values (at least 8 chars to avoid false positives)
                if (!string.IsNullOrEmpty(value) && value.Length >= 8)
                {
                    secrets.Add(value);
                }
            }
        }

        return secrets;
    }

    /// <summary>
    /// Scrubs all recognized sensitive patterns from the input text.
    /// Also scrubs any values matching known sensitive environment variables.
    /// </summary>
    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        var result = text;

        // PRIORITY 1: Scrub known secret values from environment variables
        // This catches secrets even if they don't match standard patterns
        foreach (var secret in _cachedSecretValues.Value)
        {
            if (result.Contains(secret, StringComparison.Ordinal))
            {
                result = result.Replace(secret, RedactedPlaceholder);
            }
        }

        // PRIORITY 2: Apply pattern-based scrubbing (catches any remaining patterns)
        result = OpenAiKeyPattern().Replace(result, RedactedPlaceholder);
        result = GitHubTokenPattern().Replace(result, RedactedPlaceholder);
        result = BearerTokenPattern().Replace(result, $"Bearer {RedactedPlaceholder}");

        // Generic secret pattern preserves the key name for debugging
        result = GenericSecretPattern().Replace(result, m =>
            $"{m.Groups[1].Value}{m.Groups[2].Value}{RedactedPlaceholder}");

        // Hex keys preceded by key-like words
        result = HexKeyPattern().Replace(result, m =>
            $"{m.Groups[0].Value.Split('=')[0].Split(':')[0]}{m.Groups[1].Value}{RedactedPlaceholder}");

        return result;
    }
}

/// <summary>
/// Debug logger for AI requests. Enable via SDK_CLI_DEBUG=true.
/// All output is scrubbed for secrets before writing.
/// </summary>
public class AiDebugLogger
{
    private readonly ILogger<AiDebugLogger> _logger;
    private readonly string _debugDir;
    private readonly bool _enabled;

    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    public AiDebugLogger(ILogger<AiDebugLogger> logger, SdkChatOptions options)
    {
        _logger = logger;
        _enabled = options.DebugEnabled;
        _debugDir = options.DebugDirectory ?? GetDefaultDebugDir();

        if (_enabled)
        {
            Directory.CreateDirectory(_debugDir);
            _logger.LogInformation("AI Debug logging enabled. Logs will be written to: {DebugDir}", _debugDir);
        }
    }

    private static string GetDefaultDebugDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".sdk-chat", "debug");
    }

    /// <summary>
    /// Log an AI request before it's sent.
    /// </summary>
    public AiDebugSession StartSession(
        string provider,
        string model,
        string? endpoint,
        string systemPrompt,
        string userPrompt,
        ContextInfo? contextInfo = null)
    {
        if (!_enabled) return new AiDebugSession(null, null);

        var session = new AiDebugSession(
            _debugDir,
            $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}"[..31]  // truncate to reasonable length
        );

        session.Provider = provider;
        session.Model = model;
        session.Endpoint = endpoint;
        session.SystemPrompt = systemPrompt;
        session.UserPrompt = userPrompt;
        session.ContextInfo = contextInfo;
        session.StartTime = DateTime.UtcNow;

        _logger.LogDebug("Started debug session {SessionId}", session.SessionId);

        return session;
    }

    public async Task CompleteSessionAsync(
        AiDebugSession session,
        string response,
        bool streaming,
        int? promptTokens = null,
        int? completionTokens = null,
        Exception? error = null)
    {
        if (!_enabled || session.FilePath == null) return;

        session.Response = response;
        session.Streaming = streaming;
        session.PromptTokens = promptTokens;
        session.CompletionTokens = completionTokens;
        session.Error = error;
        session.EndTime = DateTime.UtcNow;

        // Stream directly to file to handle large content
        await using var writer = new StreamWriter(session.FilePath, false, Encoding.UTF8);
        await WriteMarkdownAsync(writer, session);

        _logger.LogDebug("Wrote debug log to {FilePath}", session.FilePath);
    }

    private async Task WriteMarkdownAsync(StreamWriter writer, AiDebugSession session)
    {
        // Header
        await writer.WriteLineAsync($"# AI Debug Log: {session.SessionId}");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync($"**Generated:** {session.StartTime:yyyy-MM-dd HH:mm:ss} UTC");
        await writer.WriteLineAsync();

        // Summary Table
        await writer.WriteLineAsync("## Summary");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync("| Property | Value |");
        await writer.WriteLineAsync("|----------|-------|");
        await writer.WriteLineAsync($"| Provider | {session.Provider} |");
        await writer.WriteLineAsync($"| Model | {session.Model} |");
        await writer.WriteLineAsync($"| Endpoint | {session.Endpoint ?? "(default)"} |");
        await writer.WriteLineAsync($"| Streaming | {session.Streaming} |");
        await writer.WriteLineAsync($"| Duration | {(session.EndTime - session.StartTime)?.TotalMilliseconds:F0}ms |");
        await writer.WriteLineAsync($"| Status | {(session.Error == null ? "✅ Success" : "❌ Error")} |");
        await writer.WriteLineAsync();

        // Token usage if available
        if (session.PromptTokens.HasValue || session.CompletionTokens.HasValue)
        {
            await writer.WriteLineAsync("### Token Usage");
            await writer.WriteLineAsync();
            await writer.WriteLineAsync("| Type | Count |");
            await writer.WriteLineAsync("|------|-------|");
            if (session.PromptTokens.HasValue)
                await writer.WriteLineAsync($"| Prompt Tokens | {session.PromptTokens:N0} |");
            if (session.CompletionTokens.HasValue)
                await writer.WriteLineAsync($"| Completion Tokens | {session.CompletionTokens:N0} |");
            if (session.PromptTokens.HasValue && session.CompletionTokens.HasValue)
                await writer.WriteLineAsync($"| **Total** | **{session.PromptTokens + session.CompletionTokens:N0}** |");
            await writer.WriteLineAsync();
        }

        // Context Information
        if (session.ContextInfo != null)
        {
            await writer.WriteLineAsync("## Context Files");
            await writer.WriteLineAsync();

            if (session.ContextInfo.Files.Count > 0)
            {
                await writer.WriteLineAsync("| File | Size | Status |");
                await writer.WriteLineAsync("|------|------|--------|");

                foreach (var file in session.ContextInfo.Files.OrderByDescending(f => f.OriginalSize))
                {
                    var status = file.WasTruncated
                        ? $"⚠️ Truncated ({file.TruncatedSize:N0}/{file.OriginalSize:N0} chars, {file.TruncationPercent:F0}%)"
                        : "✅ Full";
                    await writer.WriteLineAsync($"| `{file.RelativePath}` | {FormatBytes(file.OriginalSize)} | {status} |");
                }
                await writer.WriteLineAsync();
            }

            // Context stats
            await writer.WriteLineAsync("### Context Statistics");
            await writer.WriteLineAsync();
            await writer.WriteLineAsync($"- **Total Files:** {session.ContextInfo.TotalFiles}");
            await writer.WriteLineAsync($"- **Files Included:** {session.ContextInfo.FilesIncluded}");
            await writer.WriteLineAsync($"- **Files Truncated:** {session.ContextInfo.FilesTruncated}");
            await writer.WriteLineAsync($"- **Files Skipped:** {session.ContextInfo.FilesSkipped}");
            await writer.WriteLineAsync($"- **Total Context Size:** {FormatBytes(session.ContextInfo.TotalContextSize)}");
            await writer.WriteLineAsync($"- **Max Context Size:** {FormatBytes(session.ContextInfo.MaxContextSize)}");
            await writer.WriteLineAsync();
        }

        // System Prompt
        // SECURITY: Scrub sensitive data before writing
        await writer.WriteLineAsync("## System Prompt");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync("```");
        await writer.WriteLineAsync(SensitiveDataScrubber.Scrub(session.SystemPrompt));
        await writer.WriteLineAsync("```");
        await writer.WriteLineAsync();

        // User Prompt - FULL content, no truncation
        // SECURITY: Scrub sensitive data before writing
        var scrubbedUserPrompt = SensitiveDataScrubber.Scrub(session.UserPrompt);
        await writer.WriteLineAsync("## User Prompt");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync($"**Length:** {session.UserPrompt.Length:N0} characters (~{EstimateTokens(session.UserPrompt):N0} tokens)");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync("```");
        await writer.WriteAsync(scrubbedUserPrompt);  // Stream scrubbed content
        await writer.WriteLineAsync();
        await writer.WriteLineAsync("```");
        await writer.WriteLineAsync();

        // Response - FULL content, no truncation
        await writer.WriteLineAsync("## Response");
        await writer.WriteLineAsync();

        if (session.Error != null)
        {
            await writer.WriteLineAsync("### ❌ Error");
            await writer.WriteLineAsync();
            await writer.WriteLineAsync("```");
            // SECURITY: Scrub error messages too (may contain secrets in stack traces)
            await writer.WriteLineAsync(SensitiveDataScrubber.Scrub(session.Error.ToString()));
            await writer.WriteLineAsync("```");
        }
        else
        {
            // SECURITY: Scrub response before writing
            var scrubbedResponse = SensitiveDataScrubber.Scrub(session.Response ?? "(empty)");
            await writer.WriteLineAsync($"**Length:** {session.Response?.Length ?? 0:N0} characters (~{EstimateTokens(session.Response ?? ""):N0} tokens)");
            await writer.WriteLineAsync();
            await writer.WriteLineAsync("```");
            await writer.WriteAsync(scrubbedResponse);  // Stream scrubbed content
            await writer.WriteLineAsync();
            await writer.WriteLineAsync("```");
        }
        await writer.WriteLineAsync();

        // Request Details (for debugging purposes)
        await writer.WriteLineAsync("## Request Details");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync("```json");
#pragma warning disable IL2026, IL3050 // Anonymous types used for debug logging only
        await writer.WriteLineAsync(JsonSerializer.Serialize(new
        {
            provider = session.Provider,
            model = session.Model,
            endpoint = session.Endpoint ?? "(default)",
            streaming = session.Streaming,
            systemPromptHash = ComputeHash(session.SystemPrompt),
            userPromptHash = ComputeHash(session.UserPrompt),
            userPromptLength = session.UserPrompt.Length,
            responseHash = ComputeHash(session.Response ?? ""),
            responseLength = session.Response?.Length ?? 0,
            durationMs = (session.EndTime - session.StartTime)?.TotalMilliseconds
        }, IndentedOptions));
#pragma warning restore IL2026, IL3050
        await writer.WriteLineAsync("```");
        await writer.WriteLineAsync();

        // Footer
        await writer.WriteLineAsync("---");
        await writer.WriteLineAsync($"*Debug log generated by SDK Chat v1.0.0*");
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }

    private static int EstimateTokens(string text)
    {
        // Rough estimate: ~4 characters per token
        return text.Length / 4;
    }

    private static string ComputeHash(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).Substring(0, 16).ToLowerInvariant();
    }
}

/// <summary>
/// Represents an active debug session for tracking request/response.
/// </summary>
public class AiDebugSession
{
    public string? SessionId { get; }
    public string? FilePath { get; }

    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string? Endpoint { get; set; }
    public string SystemPrompt { get; set; } = "";
    public string UserPrompt { get; set; } = "";
    public string? Response { get; set; }
    public bool Streaming { get; set; }
    public ContextInfo? ContextInfo { get; set; }

    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }

    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public Exception? Error { get; set; }

    public AiDebugSession(string? debugDir, string? sessionId)
    {
        SessionId = sessionId;
        FilePath = sessionId != null && debugDir != null
            ? Path.Combine(debugDir, $"{sessionId}.md")
            : null;
    }
}

/// <summary>
/// Information about files included in the context.
/// </summary>
public class ContextInfo
{
    public List<ContextFileInfo> Files { get; set; } = new();
    public int TotalFiles { get; set; }
    public int FilesIncluded { get; set; }
    public int FilesTruncated { get; set; }
    public int FilesSkipped { get; set; }
    public long TotalContextSize { get; set; }
    public long MaxContextSize { get; set; }
}

/// <summary>
/// Information about a single file in the context.
/// </summary>
public class ContextFileInfo
{
    public string RelativePath { get; set; } = "";
    public long OriginalSize { get; set; }
    public long TruncatedSize { get; set; }
    public bool WasTruncated { get; set; }

    public double TruncationPercent => OriginalSize > 0
        ? (1.0 - (double)TruncatedSize / OriginalSize) * 100
        : 0;
}
