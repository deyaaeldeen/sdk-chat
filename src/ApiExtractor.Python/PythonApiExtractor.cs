// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
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
        Path.GetDirectoryName(typeof(PythonApiExtractor).Assembly.Location) ?? ".",
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
            ? JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true })
            : index.ToJson();

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

        // Enforce configurable timeout (SDK_CHAT_EXTRACTOR_TIMEOUT, default 5 min)
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ExtractorTimeout.Value);
        var effectiveCt = timeoutCts.Token;

        using var processActivity = ExtractorTelemetry.StartProcess(python, Language);

        var psi = new ProcessStartInfo
        {
            FileName = python,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add(rootPath);
        psi.ArgumentList.Add("--json");

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start Python");

        // Read streams in parallel to prevent deadlocks when buffer fills
        var outputTask = process.StandardOutput.ReadToEndAsync(effectiveCt);
        var errorTask = process.StandardError.ReadToEndAsync(effectiveCt);
        await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(effectiveCt)).ConfigureAwait(false);
        var output = await outputTask;
        var error = await errorTask;

        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startTime);
        ExtractorTelemetry.RecordProcessResult(processActivity, process.ExitCode, elapsed);

        if (process.ExitCode != 0)
        {
            ExtractorTelemetry.RecordResult(activity, false, error: $"Python extractor failed: {error}");
            throw new InvalidOperationException($"Python extractor failed: {error}");
        }

        // Parse JSON output
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var raw = JsonSerializer.Deserialize<RawApiIndex>(output, options)
            ?? throw new InvalidOperationException("Failed to parse Python extractor output");

        var result = ConvertToApiIndex(raw);
        ExtractorTelemetry.RecordResult(activity, true, result.Modules.Count);
        return result;
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

    // Raw JSON models for deserialization
    private record RawApiIndex(string? Package, List<RawModule>? Modules);
    private record RawModule(string? Name, List<RawClass>? Classes, List<RawFunction>? Functions);
    private record RawClass(string? Name, string? Base, string? Doc, List<RawMethod>? Methods, List<RawProperty>? Properties);
    private record RawMethod(string? Name, string? Sig, string? Doc, bool? Async, bool? Classmethod, bool? Staticmethod);
    private record RawProperty(string? Name, string? Type, string? Doc);
    private record RawFunction(string? Name, string? Sig, string? Doc, bool? Async);
}
