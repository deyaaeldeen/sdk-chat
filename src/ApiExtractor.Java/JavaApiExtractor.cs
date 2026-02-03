// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using ApiExtractor.Contracts;

namespace ApiExtractor.Java;

/// <summary>
/// Extracts public API surface from Java packages using JBang + JavaParser.
/// </summary>
public class JavaApiExtractor : IApiExtractor<ApiIndex>
{
    private static readonly string[] JBangCandidates = { "jbang" };

    private ExtractorAvailabilityResult? _availability;

    /// <inheritdoc />
    public string Language => "java";

    /// <summary>
    /// Warning message from tool resolution (if any).
    /// </summary>
    public string? Warning => GetAvailability().Warning;

    /// <summary>
    /// Gets the current execution mode (NativeBinary or RuntimeInterpreter).
    /// </summary>
    public ExtractorMode Mode => GetAvailability().Mode;

    /// <inheritdoc />
    public bool IsAvailable() => GetAvailability().IsAvailable;

    /// <inheritdoc />
    public string? UnavailableReason => GetAvailability().UnavailableReason;

    /// <summary>
    /// Gets detailed availability information with caching.
    /// </summary>
    private ExtractorAvailabilityResult GetAvailability()
    {
        return _availability ??= ExtractorAvailability.Check(
            language: "java",
            nativeBinaryName: "java_extractor",
            runtimeToolName: "jbang",
            runtimeCandidates: JBangCandidates,
            nativeValidationArgs: "--help",
            runtimeValidationArgs: "--version");
    }

    /// <inheritdoc />
    public string ToJson(ApiIndex index, bool pretty = false)
        => pretty
            ? JsonSerializer.Serialize(index, SourceGenerationContext.Indented.ApiIndex)
            : JsonSerializer.Serialize(index, SourceGenerationContext.Default.ApiIndex);

    /// <inheritdoc />
    public string ToStubs(ApiIndex index) => JavaFormatter.Format(index);

    /// <inheritdoc />
    async Task<ExtractorResult<ApiIndex>> IApiExtractor<ApiIndex>.ExtractAsync(string rootPath, CancellationToken ct)
    {
        if (!IsAvailable())
            return ExtractorResult<ApiIndex>.CreateFailure(UnavailableReason ?? "JBang not available");

        try
        {
            var result = await ExtractAsync(rootPath, ct).ConfigureAwait(false);
            return result != null
                ? ExtractorResult<ApiIndex>.CreateSuccess(result)
                : ExtractorResult<ApiIndex>.CreateFailure("No API surface extracted");
        }
        catch (Exception ex)
        {
            return ExtractorResult<ApiIndex>.CreateFailure($"{ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>
    /// Extract API from a Java package directory.
    /// Prefers pre-compiled binary from build, falls back to JBang runtime.
    /// </summary>
    public async Task<ApiIndex?> ExtractAsync(string rootPath, CancellationToken ct = default)
    {
        var availability = GetAvailability();

        if (availability.Mode == ExtractorMode.NativeBinary)
        {
            // Use precompiled native binary
            var result = await ProcessSandbox.ExecuteAsync(
                availability.ExecutablePath!,
                [rootPath, "--json"],
                cancellationToken: ct
            ).ConfigureAwait(false);

            if (!result.Success)
            {
                var errorMsg = result.TimedOut
                    ? $"Java extractor timed out after {ExtractorTimeout.Value.TotalSeconds}s"
                    : $"Java extractor failed: {result.StandardError}";
                throw new InvalidOperationException(errorMsg);
            }

            if (string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                return null;
            }

            return JsonSerializer.Deserialize(result.StandardOutput, SourceGenerationContext.Default.ApiIndex);
        }

        if (availability.Mode != ExtractorMode.RuntimeInterpreter)
        {
            throw new InvalidOperationException(availability.UnavailableReason ?? "Java extractor not available");
        }

        // Fall back to JBang runtime
        var scriptPath = GetScriptPath();
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException($"ExtractApi.java not found at {scriptPath}");
        }

        // Use ProcessSandbox for hardened execution with timeout and output limits
        var jbangResult = await ProcessSandbox.ExecuteAsync(
            availability.ExecutablePath!,
            [scriptPath, rootPath, "--json"],
            cancellationToken: ct
        ).ConfigureAwait(false);

        if (!jbangResult.Success)
        {
            var errorMsg = jbangResult.TimedOut
                ? $"Java extractor timed out after {ExtractorTimeout.Value.TotalSeconds}s"
                : $"jbang failed: {jbangResult.StandardError}";
            throw new InvalidOperationException(errorMsg);
        }

        if (string.IsNullOrWhiteSpace(jbangResult.StandardOutput))
        {
            return null;
        }

        return JsonSerializer.Deserialize(jbangResult.StandardOutput, SourceGenerationContext.Default.ApiIndex);
    }

    /// <summary>
    /// Extract and format as Java stub syntax.
    /// </summary>
    public async Task<string> ExtractAsJavaAsync(string rootPath, CancellationToken ct = default)
    {
        var availability = GetAvailability();

        if (availability.Mode == ExtractorMode.NativeBinary)
        {
            // Use precompiled native binary
            var result = await ProcessSandbox.ExecuteAsync(
                availability.ExecutablePath!,
                [rootPath, "--stub"],
                cancellationToken: ct
            ).ConfigureAwait(false);

            if (!result.Success)
            {
                var errorMsg = result.TimedOut
                    ? $"Java extractor timed out after {ExtractorTimeout.Value.TotalSeconds}s"
                    : $"Java extractor failed: {result.StandardError}";
                throw new InvalidOperationException(errorMsg);
            }

            return result.StandardOutput;
        }

        if (availability.Mode != ExtractorMode.RuntimeInterpreter)
        {
            throw new InvalidOperationException(availability.UnavailableReason ?? "Java extractor not available");
        }

        // Fall back to JBang runtime
        var scriptPath = GetScriptPath();

        // Use ProcessSandbox for hardened execution with timeout and output limits
        var jbangResult = await ProcessSandbox.ExecuteAsync(
            availability.ExecutablePath!,
            [scriptPath, rootPath, "--stub"],
            cancellationToken: ct
        ).ConfigureAwait(false);

        if (!jbangResult.Success)
        {
            var errorMsg = jbangResult.TimedOut
                ? $"Java extractor timed out after {ExtractorTimeout.Value.TotalSeconds}s"
                : $"jbang failed: {jbangResult.StandardError}";
            throw new InvalidOperationException(errorMsg);
        }

        return jbangResult.StandardOutput;
    }

    private static string GetScriptPath()
    {
        // SECURITY: Only load scripts from assembly directory - no path traversal allowed
        var assemblyDir = AppContext.BaseDirectory;
        var scriptPath = Path.Combine(assemblyDir, "ExtractApi.java");

        if (File.Exists(scriptPath))
            return scriptPath;

        throw new FileNotFoundException(
            $"Corrupt installation: ExtractApi.java not found at {scriptPath}. " +
            "Reinstall the application to resolve this issue.");
    }
}
