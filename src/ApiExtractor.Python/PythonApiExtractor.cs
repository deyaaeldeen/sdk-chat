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

    private ExtractorAvailabilityResult? _availability;

    /// <inheritdoc />
    public string Language => "python";

    /// <summary>
    /// Warning message from tool resolution (if any).
    /// Check this after calling IsAvailable() for non-fatal issues.
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
            language: "python",
            nativeBinaryName: "python_extractor",
            runtimeToolName: "python",
            runtimeCandidates: PythonCandidates,
            nativeValidationArgs: "--help",
            runtimeValidationArgs: "--version");
    }

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
        rootPath = ProcessSandbox.ValidateRootPath(rootPath);
        using var activity = ExtractorTelemetry.StartExtraction(Language, rootPath);
        var startTime = System.Diagnostics.Stopwatch.GetTimestamp();

        var availability = GetAvailability();
        ProcessResult result;

        if (availability.Mode == ExtractorMode.NativeBinary)
        {
            // Use precompiled native binary
            result = await ProcessSandbox.ExecuteAsync(
                availability.ExecutablePath!,
                [rootPath, "--json"],
                cancellationToken: ct
            ).ConfigureAwait(false);
        }
        else if (availability.Mode == ExtractorMode.RuntimeInterpreter)
        {
            // Fall back to Python runtime
            var scriptPath = GetScriptPath();

            result = await ProcessSandbox.ExecuteAsync(
                availability.ExecutablePath!,
                [scriptPath, rootPath, "--json"],
                cancellationToken: ct
            ).ConfigureAwait(false);
        }
        else if (availability.Mode == ExtractorMode.Docker)
        {
            // Fall back to Docker container with precompiled extractor
            result = await DockerSandbox.ExecuteAsync(
                availability.DockerImageName!,
                rootPath,
                [rootPath, "--json"],
                cancellationToken: ct
            ).ConfigureAwait(false);
        }
        else
        {
            throw new InvalidOperationException(availability.UnavailableReason ?? "Python extractor not available");
        }

        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startTime);

        if (!result.Success)
        {
            var modeHint = availability.Mode == ExtractorMode.Docker ? " (docker)" : "";
            var errorMsg = result.TimedOut
                ? $"Python extractor{modeHint} timed out after {ExtractorTimeout.Value.TotalSeconds}s"
                : $"Python extractor{modeHint} failed: {result.StandardError}";
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

    private static ApiIndex ConvertToApiIndex(RawPythonApiIndex raw)
    {
        var modules = raw.Modules?.Select(m => new ModuleInfo(
            m.Name ?? "",
            m.Classes?.Select(c => new ClassInfo
            {
                Name = c.Name ?? "",
                EntryPoint = c.EntryPoint,
                ReExportedFrom = c.ReExportedFrom,
                Base = c.Base,
                Doc = c.Doc,
                Methods = c.Methods?.Select(mt => new MethodInfo(
                    mt.Name ?? "",
                    mt.Sig ?? "",
                    mt.Doc,
                    mt.Async,
                    mt.Classmethod,
                    mt.Staticmethod
                )).ToList(),
                Properties = c.Properties?.Select(p => new PropertyInfo(p.Name ?? "", p.Type, p.Doc)).ToList()
            }).ToList(),
            m.Functions?.Select(f => new FunctionInfo
            {
                Name = f.Name ?? "",
                EntryPoint = f.EntryPoint,
                ReExportedFrom = f.ReExportedFrom,
                Signature = f.Sig ?? "",
                Doc = f.Doc,
                IsAsync = f.Async
            }).ToList()
        )).ToList() ?? [];

        return new ApiIndex(raw.Package ?? "", modules);
    }

    // AOT-safe deserialization using source-generated context
    private static RawPythonApiIndex? DeserializeRaw(string json) =>
        JsonSerializer.Deserialize(json, ExtractorJsonContext.Default.RawPythonApiIndex);
}
