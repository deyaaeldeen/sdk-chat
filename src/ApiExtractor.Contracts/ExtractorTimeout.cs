// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace ApiExtractor.Contracts;

/// <summary>
/// Centralized timeout configuration for all API extractors.
/// Reads from environment variable SDK_CHAT_EXTRACTOR_TIMEOUT.
/// </summary>
public static class ExtractorTimeout
{
    /// <summary>
    /// Default timeout in seconds (5 minutes for large repos).
    /// </summary>
    public const int DefaultSeconds = 300;

    /// <summary>
    /// Environment variable name for timeout override.
    /// </summary>
    public const string EnvVarName = "SDK_CHAT_EXTRACTOR_TIMEOUT";

    private static Lazy<TimeSpan> _lazy = new(ResolveTimeout);

    /// <summary>
    /// Gets the configured extractor timeout.
    /// Thread-safe; reads SDK_CHAT_EXTRACTOR_TIMEOUT once and caches the result.
    /// </summary>
    public static TimeSpan Value => _lazy.Value;

    private static TimeSpan ResolveTimeout()
    {
        var envValue = Environment.GetEnvironmentVariable(EnvVarName);
        if (int.TryParse(envValue, out var seconds) && seconds > 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }
        return TimeSpan.FromSeconds(DefaultSeconds);
    }

    /// <summary>
    /// Resets the cached timeout. For testing only.
    /// </summary>
    internal static void Reset() => _lazy = new(ResolveTimeout);
}
