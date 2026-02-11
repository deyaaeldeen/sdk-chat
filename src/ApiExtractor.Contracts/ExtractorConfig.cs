// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace ApiExtractor.Contracts;

/// <summary>
/// Immutable configuration for a language-specific API extractor.
/// Defines all parameters needed for availability detection across
/// both the extractor and its paired usage analyzer.
/// </summary>
/// <remarks>
/// Each language defines one static <see cref="ExtractorConfig"/> instance,
/// eliminating the duplicated candidates arrays, field names, and
/// <see cref="ExtractorAvailability.Check"/> call sites that were
/// previously copy-pasted across every extractor + analyzer pair.
/// </remarks>
public sealed record ExtractorConfig
{
    /// <summary>Language identifier (e.g., "python", "java", "go", "typescript").</summary>
    public required string Language { get; init; }

    /// <summary>Name of the precompiled native binary (e.g., "python_extractor").</summary>
    public required string NativeBinaryName { get; init; }

    /// <summary>Name of the runtime tool (e.g., "python", "jbang", "node", "go").</summary>
    public required string RuntimeToolName { get; init; }

    /// <summary>Candidate paths for the runtime tool.</summary>
    public required string[] RuntimeCandidates { get; init; }

    /// <summary>Arguments passed to the native binary for validation (default: "--help").</summary>
    public string NativeValidationArgs { get; init; } = "--help";

    /// <summary>Arguments passed to the runtime tool for validation (default: "--version").</summary>
    public string RuntimeValidationArgs { get; init; } = "--version";
}

/// <summary>
/// Provides cached extractor availability from an <see cref="ExtractorConfig"/>.
/// Shared between an extractor and its paired usage analyzer to ensure
/// a single availability check per language, with consistent configuration.
/// </summary>
public sealed class ExtractorAvailabilityProvider
{
    private readonly ExtractorConfig _config;
    private ExtractorAvailabilityResult? _cached;

    public ExtractorAvailabilityProvider(ExtractorConfig config)
    {
        _config = config;
    }

    /// <summary>Gets the language identifier.</summary>
    public string Language => _config.Language;

    /// <summary>Checks if the extractor is available.</summary>
    public bool IsAvailable => GetAvailability().IsAvailable;

    /// <summary>Gets a message describing why the extractor is unavailable.</summary>
    public string? UnavailableReason => GetAvailability().UnavailableReason;

    /// <summary>Warning message from tool resolution (if any).</summary>
    public string? Warning => GetAvailability().Warning;

    /// <summary>Gets the current execution mode.</summary>
    public ExtractorMode Mode => GetAvailability().Mode;

    /// <summary>Gets the full availability result with caching.</summary>
    public ExtractorAvailabilityResult GetAvailability()
    {
        return _cached ??= ExtractorAvailability.Check(
            language: _config.Language,
            nativeBinaryName: _config.NativeBinaryName,
            runtimeToolName: _config.RuntimeToolName,
            runtimeCandidates: _config.RuntimeCandidates,
            nativeValidationArgs: _config.NativeValidationArgs,
            runtimeValidationArgs: _config.RuntimeValidationArgs);
    }
}
