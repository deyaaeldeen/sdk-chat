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

    private string? _jbangPath;
    private string? _unavailableReason;
    private string? _warning;

    /// <inheritdoc />
    public string Language => "java";

    /// <summary>
    /// Warning message from tool resolution (if any).
    /// </summary>
    public string? Warning => _warning;

    /// <inheritdoc />
    public bool IsAvailable()
    {
        var result = ToolPathResolver.ResolveWithDetails("jbang", JBangCandidates);
        if (!result.IsAvailable)
        {
            _unavailableReason = "JBang not found. Install JBang (https://jbang.dev) and ensure it's in PATH.";
            return false;
        }
        _jbangPath = result.Path;
        _warning = result.WarningOrError; // Store warning for structured logging by caller
        return true;
    }

    /// <inheritdoc />
    public string? UnavailableReason => _unavailableReason;

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
    /// </summary>
    public async Task<ApiIndex?> ExtractAsync(string rootPath, CancellationToken ct = default)
    {
        var jbangPath = _jbangPath ?? ToolPathResolver.Resolve("jbang", JBangCandidates)
            ?? throw new InvalidOperationException("JBang not found");

        var scriptPath = GetScriptPath();
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException($"ExtractApi.java not found at {scriptPath}");
        }

        // Use ProcessSandbox for hardened execution with timeout and output limits
        var result = await ProcessSandbox.ExecuteAsync(
            jbangPath,
            [scriptPath, rootPath, "--json"],
            cancellationToken: ct
        ).ConfigureAwait(false);

        if (!result.Success)
        {
            var errorMsg = result.TimedOut
                ? $"Java extractor timed out after {ExtractorTimeout.Value.TotalSeconds}s"
                : $"jbang failed: {result.StandardError}";
            throw new InvalidOperationException(errorMsg);
        }

        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return null;
        }

        return JsonSerializer.Deserialize(result.StandardOutput, SourceGenerationContext.Default.ApiIndex);
    }

    /// <summary>
    /// Extract and format as Java stub syntax.
    /// </summary>
    public async Task<string> ExtractAsJavaAsync(string rootPath, CancellationToken ct = default)
    {
        var jbangPath = _jbangPath ?? ToolPathResolver.Resolve("jbang", JBangCandidates)
            ?? throw new InvalidOperationException("JBang not found");

        var scriptPath = GetScriptPath();

        // Use ProcessSandbox for hardened execution with timeout and output limits
        var result = await ProcessSandbox.ExecuteAsync(
            jbangPath,
            [scriptPath, rootPath, "--stub"],
            cancellationToken: ct
        ).ConfigureAwait(false);

        if (!result.Success)
        {
            var errorMsg = result.TimedOut
                ? $"Java extractor timed out after {ExtractorTimeout.Value.TotalSeconds}s"
                : $"jbang failed: {result.StandardError}";
            throw new InvalidOperationException(errorMsg);
        }

        return result.StandardOutput;
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
