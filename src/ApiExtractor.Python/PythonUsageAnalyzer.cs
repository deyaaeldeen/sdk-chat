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

    private static readonly string[] PythonCandidates = { "python3", "python" };

    private ExtractorAvailabilityResult? _availability;

    /// <inheritdoc />
    public string Language => "python";

    /// <summary>
    /// Checks if Python is available to run the usage analyzer.
    /// </summary>
    public bool IsAvailable() => GetAvailability().IsAvailable;

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

        var availability = GetAvailability();
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
                UncoveredOperations = result.Uncovered?.Select(u => new UncoveredOperation
                {
                    ClientType = u.Client ?? "",
                    Operation = u.Method ?? "",
                    Signature = u.Sig ?? $"{u.Method}(...)"
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

            return result.Success ? result.StandardOutput : null;
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

        return runtimeResult.Success ? runtimeResult.StandardOutput : null;
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
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var references = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var cls in allClasses)
        {
            var name = cls.Name.Split('[')[0];
            references[name] = cls.GetReferencedTypes(allTypeNames);
        }

        var referencedBy = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var refs in references.Values)
        {
            foreach (var target in refs)
            {
                referencedBy[target] = referencedBy.TryGetValue(target, out var count) ? count + 1 : 1;
            }
        }

        var operationTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cls in allClasses)
        {
            if (cls.Methods?.Any() ?? false)
            {
                operationTypes.Add(cls.Name.Split('[')[0]);
            }
        }

        var rootClasses = allClasses
            .Where(cls =>
            {
                var name = cls.Name.Split('[')[0];
                var hasOperations = cls.Methods?.Any() ?? false;
                var referencesOperations = references.TryGetValue(name, out var refs) && refs.Any(operationTypes.Contains);
                var isReferenced = referencedBy.ContainsKey(name);
                return !isReferenced && (hasOperations || referencesOperations);
            })
            .ToList();

        if (rootClasses.Count == 0)
        {
            rootClasses = allClasses
                .Where(cls =>
                {
                    var name = cls.Name.Split('[')[0];
                    var hasOperations = cls.Methods?.Any() ?? false;
                    var referencesOperations = references.TryGetValue(name, out var refs) && refs.Any(operationTypes.Contains);
                    return hasOperations || referencesOperations;
                })
                .ToList();
        }

        var derivedByBase = new Dictionary<string, List<ClassInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var cls in allClasses)
        {
            if (string.IsNullOrWhiteSpace(cls.Base))
            {
                continue;
            }

            var baseName = cls.Base.Split('[')[0];
            if (!derivedByBase.TryGetValue(baseName, out var list))
            {
                list = [];
                derivedByBase[baseName] = list;
            }
            list.Add(cls);
        }

        var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();

        foreach (var root in rootClasses)
        {
            var name = root.Name.Split('[')[0];
            if (reachable.Add(name))
            {
                queue.Enqueue(name);
            }
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            if (references.TryGetValue(current, out var refs))
            {
                foreach (var typeName in refs)
                {
                    if (reachable.Add(typeName))
                    {
                        queue.Enqueue(typeName);
                    }
                }
            }

            if (derivedByBase.TryGetValue(current, out var derived))
            {
                foreach (var child in derived)
                {
                    var childName = child.Name.Split('[')[0];
                    if (reachable.Add(childName))
                    {
                        queue.Enqueue(childName);
                    }
                }
            }
        }

        return allClasses
            .Where(c => reachable.Contains(c.Name.Split('[')[0]) && (c.Methods?.Any() ?? false))
            .GroupBy(c => c.Name.Split('[')[0], StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }
}
