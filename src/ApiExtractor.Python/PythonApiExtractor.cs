// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using ApiExtractor.Contracts;

namespace ApiExtractor.Python;

/// <summary>
/// Extracts public API surface from Python source files.
/// Shells out to Python's ast module for proper parsing.
/// </summary>
public class PythonApiExtractor : IApiExtractor<ApiIndex>
{
    private static readonly string ScriptPath = Path.Combine(
        AppContext.BaseDirectory,
        "extract_api.py");

    private static readonly string[] PythonCandidates = { "python3", "python" };

    private string? _pythonPath;
    private string? _unavailableReason;
    private string? _warning;

    /// <inheritdoc />
    public string Language => "python";

    /// <summary>
    /// Warning message from tool resolution (if any).
    /// Check this after calling IsAvailable() for non-fatal issues.
    /// </summary>
    public string? Warning => _warning;

    /// <inheritdoc />
    public bool IsAvailable()
    {
        var result = ToolPathResolver.ResolveWithDetails("python", PythonCandidates);
        if (!result.IsAvailable)
        {
            _unavailableReason = "Python 3 not found. Install Python 3.9+ and ensure it's in PATH.";
            return false;
        }
        _pythonPath = result.Path;
        _warning = result.WarningOrError; // Store warning for structured logging by caller
        return true;
    }

    /// <inheritdoc />
    public string? UnavailableReason => _unavailableReason;

    /// <inheritdoc />
    public string ToJson(ApiIndex index, bool pretty = false)
        => pretty
            ? JsonSerializer.Serialize(index, ApiIndexContext.Indented.ApiIndex)
            : JsonSerializer.Serialize(index, ApiIndexContext.Default.ApiIndex);

    /// <inheritdoc />
    public string ToStubs(ApiIndex index) => PythonFormatter.Format(index);

    /// <inheritdoc />
    async Task<ExtractorResult<ApiIndex>> IApiExtractor<ApiIndex>.ExtractAsync(string rootPath, CancellationToken ct)
    {
        if (!IsAvailable())
            return ExtractorResult<ApiIndex>.CreateFailure(UnavailableReason ?? "Python not available");

        try
        {
            var result = await ExtractAsync(rootPath, ct).ConfigureAwait(false);
            return ExtractorResult<ApiIndex>.CreateSuccess(result);
        }
        catch (Exception ex)
        {
            return ExtractorResult<ApiIndex>.CreateFailure($"{ex.Message}\n{ex.StackTrace}");
        }
    }

    public async Task<ApiIndex> ExtractAsync(string rootPath, CancellationToken ct = default)
    {
        using var activity = ExtractorTelemetry.StartExtraction(Language, rootPath);
        var startTime = System.Diagnostics.Stopwatch.GetTimestamp();

        // Find python executable
        var python = _pythonPath ?? ToolPathResolver.Resolve("python", PythonCandidates);
        if (python == null)
            throw new InvalidOperationException("Python 3 not found. Install Python 3.9+ and ensure it's in PATH.");

        // Get script path - embedded in assembly directory
        var scriptPath = GetScriptPath();

        // Use ProcessSandbox for hardened execution with timeout and output limits
        var result = await ProcessSandbox.ExecuteAsync(
            python,
            [scriptPath, rootPath, "--json"],
            cancellationToken: ct
        ).ConfigureAwait(false);

        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startTime);

        if (!result.Success)
        {
            var errorMsg = result.TimedOut
                ? $"Python extractor timed out after {ExtractorTimeout.Value.TotalSeconds}s"
                : $"Python extractor failed: {result.StandardError}";
            ExtractorTelemetry.RecordResult(activity, false, error: errorMsg);
            throw new InvalidOperationException(errorMsg);
        }

        // Parse JSON output
        var raw = DeserializeRaw(result.StandardOutput)
            ?? throw new InvalidOperationException("Failed to parse Python extractor output");

        var apiIndex = ConvertToApiIndex(raw);
        ExtractorTelemetry.RecordResult(activity, true, apiIndex.Modules.Count);
        return apiIndex;
    }

    private static string GetScriptPath()
    {
        // SECURITY: Only load scripts from assembly directory - no path traversal allowed
        if (File.Exists(ScriptPath))
            return ScriptPath;

        throw new FileNotFoundException(
            $"Corrupt installation: extract_api.py not found at {ScriptPath}. " +
            "Reinstall the application to resolve this issue.");
    }

    private static ApiIndex ConvertToApiIndex(RawApiIndex raw)
    {
        var modules = raw.Modules?.Select(m => new ModuleInfo(
            m.Name ?? "",
            m.Classes?.Select(c => new ClassInfo(
                c.Name ?? "",
                c.Base,
                c.Doc,
                c.Methods?.Select(mt => new MethodInfo(
                    mt.Name ?? "",
                    mt.Sig ?? "",
                    mt.Doc,
                    mt.Async,
                    mt.Classmethod,
                    mt.Staticmethod
                )).ToList(),
                c.Properties?.Select(p => new PropertyInfo(p.Name ?? "", p.Type, p.Doc)).ToList()
            )).ToList(),
            m.Functions?.Select(f => new FunctionInfo(
                f.Name ?? "",
                f.Sig ?? "",
                f.Doc,
                f.Async
            )).ToList()
        )).ToList() ?? [];

        return new ApiIndex(raw.Package ?? "", modules);
    }

    // Internal DTOs for JSON parsing - suppressions are safe as these are internal
    // utilities for parsing known JSON from our own scripts
#pragma warning disable IL2026, IL3050 // Suppressed: internal DTOs with known schema
    private static RawApiIndex? DeserializeRaw(string json) =>
        JsonSerializer.Deserialize<RawApiIndex>(json, JsonOptionsCache.CaseInsensitive);
#pragma warning restore IL2026, IL3050

    // Raw JSON models for deserialization
    private record RawApiIndex(string? Package, List<RawModule>? Modules);
    private record RawModule(string? Name, List<RawClass>? Classes, List<RawFunction>? Functions);
    private record RawClass(string? Name, string? Base, string? Doc, List<RawMethod>? Methods, List<RawProperty>? Properties);
    private record RawMethod(string? Name, string? Sig, string? Doc, bool? Async, bool? Classmethod, bool? Staticmethod);
    private record RawProperty(string? Name, string? Type, string? Doc);
    private record RawFunction(string? Name, string? Sig, string? Doc, bool? Async);
}
