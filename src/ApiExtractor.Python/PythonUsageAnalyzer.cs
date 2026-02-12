// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using ApiExtractor.Contracts;

namespace ApiExtractor.Python;

/// <summary>
/// Analyzes Python code to extract which API operations are being used.
/// Uses Python's AST module for accurate parsing via extract_api.py --usage mode.
/// </summary>
public class PythonUsageAnalyzer : IUsageAnalyzer<ApiIndex>
{
    private static readonly string ScriptPath = Path.Combine(
        AppContext.BaseDirectory,
        "extract_api.py");

    private readonly ExtractorAvailabilityProvider _availability = new(PythonApiExtractor.SharedConfig);

    /// <inheritdoc />
    public string Language => "python";

    /// <summary>
    /// Checks if Python is available to run the usage analyzer.
    /// </summary>
    public bool IsAvailable() => _availability.IsAvailable;

    /// <inheritdoc />
    public async Task<UsageIndex> AnalyzeAsync(string codePath, ApiIndex apiIndex, CancellationToken ct = default)
    {
        var normalizedPath = Path.GetFullPath(codePath);

        if (!Directory.Exists(normalizedPath))
            return new UsageIndex { FileCount = 0 };

        var clientClasses = GetClientAndSubclientClasses(apiIndex);

        // Get client classes from API - if none, no point analyzing
        if (!clientClasses.Any())
            return new UsageIndex { FileCount = 0 };

        var availability = _availability.GetAvailability();
        if (!availability.IsAvailable)
            return new UsageIndex { FileCount = 0 };

        // Write API index to temp file for the script
        var tempApiFile = Path.GetTempFileName();
        try
        {
            var apiJson = apiIndex.ToJson();
            await File.WriteAllTextAsync(tempApiFile, apiJson, ct);

            var output = await AnalyzeUsageAsync(availability, tempApiFile, normalizedPath, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(output))
                return new UsageIndex { FileCount = 0 };

            // Parse the JSON output
            var result = DeserializeResult(output);

            if (result is null)
                return new UsageIndex { FileCount = 0 };

            return new UsageIndex
            {
                FileCount = result.FileCount,
                CoveredOperations = result.Covered?.Select(c => new OperationUsage
                {
                    ClientType = c.Client ?? "",
                    Operation = c.Method ?? "",
                    File = c.File ?? "",
                    Line = c.Line
                }).ToList() ?? [],
                UncoveredOperations = result.Uncovered?.Select(u =>
                {
                    var sig = u.Sig;
                    if (sig is null)
                        sig = BuildSignatureLookup(apiIndex).GetValueOrDefault($"{u.Client}.{u.Method}") ?? $"{u.Method}(...)";
                    return new UncoveredOperation
                    {
                        ClientType = u.Client ?? "",
                        Operation = u.Method ?? "",
                        Signature = sig
                    };
                }).ToList() ?? []
            };
        }
        finally
        {
            // Best-effort cleanup: temp file deletion failure is non-critical
            // (OS will clean up temp files, and we don't want to mask the real result)
            try { File.Delete(tempApiFile); } catch { /* Intentionally ignored - temp file cleanup */ }
        }
    }

    /// <inheritdoc />
    public string Format(UsageIndex index) => UsageFormatter.Format(index);

    private static async Task<string?> AnalyzeUsageAsync(
        ExtractorAvailabilityResult availability,
        string apiJsonPath,
        string samplesPath,
        CancellationToken ct)
    {
        if (availability.Mode == ExtractorMode.NativeBinary)
        {
            var result = await ProcessSandbox.ExecuteAsync(
                availability.ExecutablePath!,
                ["--usage", apiJsonPath, samplesPath],
                cancellationToken: ct
            ).ConfigureAwait(false);

            if (!result.Success)
            {
                Console.Error.WriteLine(result.TimedOut
                    ? $"Python usage analyzer: timed out after {ExtractorTimeout.Value.TotalSeconds}s"
                    : $"Python usage analyzer: failed (exit {result.ExitCode}): {result.StandardError}");
                return null;
            }

            return result.StandardOutput;
        }

        if (availability.Mode == ExtractorMode.Docker)
        {
            var dockerResult = await DockerSandbox.ExecuteAsync(
                availability.DockerImageName!,
                [apiJsonPath, samplesPath],
                ["--usage", apiJsonPath, samplesPath],
                cancellationToken: ct
            ).ConfigureAwait(false);

            if (!dockerResult.Success)
            {
                Console.Error.WriteLine(dockerResult.TimedOut
                    ? $"Python usage analyzer: Docker timed out after {ExtractorTimeout.Value.TotalSeconds}s"
                    : $"Python usage analyzer: Docker failed: {dockerResult.StandardError}");
                return null;
            }

            return dockerResult.StandardOutput;
        }

        if (availability.Mode != ExtractorMode.RuntimeInterpreter)
            return null;

        var scriptPath = GetScriptPath();
        var runtimeResult = await ProcessSandbox.ExecuteAsync(
            availability.ExecutablePath!,
            [scriptPath, "--usage", apiJsonPath, samplesPath],
            cancellationToken: ct
        ).ConfigureAwait(false);

        if (!runtimeResult.Success)
        {
            Console.Error.WriteLine(runtimeResult.TimedOut
                ? $"Python usage analyzer: timed out after {ExtractorTimeout.Value.TotalSeconds}s"
                : $"Python usage analyzer: failed (exit {runtimeResult.ExitCode}): {runtimeResult.StandardError}");
            return null;
        }

        return runtimeResult.StandardOutput;
    }

    private static string GetScriptPath()
    {
        if (File.Exists(ScriptPath))
            return ScriptPath;

        throw new FileNotFoundException(
            $"Corrupt installation: extract_api.py not found at {ScriptPath}. " +
            "Reinstall the application to resolve this issue.");
    }

    // AOT-safe deserialization using source-generated context
    private static UsageResult? DeserializeResult(string json) =>
        JsonSerializer.Deserialize(json, ExtractorJsonContext.Default.UsageResult);

    private static IReadOnlyList<ClassInfo> GetClientAndSubclientClasses(ApiIndex apiIndex)
    {
        var allClasses = apiIndex.GetAllClasses().ToList();
        var allTypeNames = allClasses
            .Select(c => c.Name.Split('[')[0])
            .ToHashSet(StringComparer.Ordinal);

        // Build base→derived edges for BFS (Python uses inheritance, not interfaces)
        var additionalEdges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var cls in allClasses)
        {
            if (string.IsNullOrWhiteSpace(cls.Base))
                continue;

            var baseName = cls.Base.Split('[')[0];
            if (!additionalEdges.TryGetValue(baseName, out var list))
            {
                list = [];
                additionalEdges[baseName] = list;
            }
            list.Add(cls.Name.Split('[')[0]);
        }

        // Build type nodes for reachability analysis
        var typeNodes = allClasses.Select(c => new ReachabilityAnalyzer.TypeNode
        {
            Name = c.Name.Split('[')[0],
            HasOperations = c.Methods?.Any() ?? false,
            IsExplicitEntryPoint = c.EntryPoint == true,
            ReferencedTypes = c.GetReferencedTypes(allTypeNames)
        }).ToList();

        var reachable = ReachabilityAnalyzer.FindReachable(typeNodes, additionalEdges, StringComparer.Ordinal);

        return allClasses
            .Where(c => reachable.Contains(c.Name.Split('[')[0]) && (c.Methods?.Any() ?? false))
            .GroupBy(c => c.Name.Split('[')[0], StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>
    /// Builds a lookup from "TypeName.MethodName" → "MethodName(Signature)" using the API index,
    /// so uncovered operations get real signatures when the script fails to provide one.
    /// </summary>
    internal static Dictionary<string, string> BuildSignatureLookup(ApiIndex apiIndex)
    {
        var lookup = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var cls in apiIndex.GetAllClasses())
            foreach (var method in cls.Methods ?? [])
                lookup.TryAdd($"{cls.Name}.{method.Name}", $"{method.Name}({method.Signature})");
        return lookup;
    }
}
