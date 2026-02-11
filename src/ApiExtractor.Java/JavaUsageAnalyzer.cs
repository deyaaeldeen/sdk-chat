// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using ApiExtractor.Contracts;

namespace ApiExtractor.Java;

/// <summary>
/// Analyzes Java code to extract which API operations are being used.
/// Uses JavaParser (via JBang) for accurate AST-based parsing.
/// </summary>
public class JavaUsageAnalyzer : IUsageAnalyzer<ApiIndex>
{
    private readonly ExtractorAvailabilityProvider _availability = new(JavaApiExtractor.SharedConfig);

    /// <inheritdoc />
    public string Language => "java";

    /// <summary>
    /// Checks if JBang is available to run the usage analyzer.
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
            var apiJson = JsonSerializer.Serialize(apiIndex, SourceGenerationContext.Default.ApiIndex);
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

    private static string GetScriptDir() => AppContext.BaseDirectory;

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
                    ? $"Java usage analyzer: timed out after {ExtractorTimeout.Value.TotalSeconds}s"
                    : $"Java usage analyzer: failed (exit {result.ExitCode}): {result.StandardError}");
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
                    ? $"Java usage analyzer: Docker timed out after {ExtractorTimeout.Value.TotalSeconds}s"
                    : $"Java usage analyzer: Docker failed: {dockerResult.StandardError}");
                return null;
            }

            return dockerResult.StandardOutput;
        }

        if (availability.Mode != ExtractorMode.RuntimeInterpreter)
            return null;

        var scriptDir = GetScriptDir();
        var scriptPath = Path.Combine(scriptDir, "ExtractApi.java");

        var runtimeResult = await ProcessSandbox.ExecuteAsync(
            availability.ExecutablePath!,
            [scriptPath, "--usage", apiJsonPath, samplesPath],
            workingDirectory: scriptDir,
            cancellationToken: ct
        ).ConfigureAwait(false);

        if (!runtimeResult.Success)
        {
            Console.Error.WriteLine(runtimeResult.TimedOut
                ? $"Java usage analyzer: timed out after {ExtractorTimeout.Value.TotalSeconds}s"
                : $"Java usage analyzer: failed (exit {runtimeResult.ExitCode}): {runtimeResult.StandardError}");
            return null;
        }

        return runtimeResult.StandardOutput;
    }

    // AOT-safe deserialization using source-generated context
    private static UsageResult? DeserializeResult(string json) =>
        JsonSerializer.Deserialize(json, ExtractorJsonContext.Default.UsageResult);

    private static IReadOnlyList<ClassInfo> GetClientAndSubclientClasses(ApiIndex apiIndex)
    {
        var concreteClasses = apiIndex.Packages
            .SelectMany(p => p.Classes ?? [])
            .ToList();

        var allInterfaces = apiIndex.Packages
            .SelectMany(p => p.Interfaces ?? [])
            .ToList();

        var allClasses = concreteClasses
            .Concat(allInterfaces)
            .ToList();

        var allTypeNames = allClasses
            .Select(c => c.Name.Split('<')[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var interfaceNames = apiIndex.Packages
            .SelectMany(p => p.Interfaces ?? [])
            .Select(i => i.Name.Split('<')[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Build interface→implementer edges for BFS
        var additionalEdges = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var cls in allClasses)
        {
            foreach (var iface in cls.Implements ?? [])
            {
                var ifaceName = iface.Split('<')[0];
                if (!additionalEdges.TryGetValue(ifaceName, out var list))
                {
                    list = [];
                    additionalEdges[ifaceName] = list;
                }
                list.Add(cls.Name.Split('<')[0]);
            }
        }

        // Build type nodes for reachability analysis
        var typeNodes = allClasses.Select(c => new ReachabilityAnalyzer.TypeNode
        {
            Name = c.Name.Split('<')[0],
            HasOperations = c.Methods?.Any() ?? false,
            IsRootCandidate = !interfaceNames.Contains(c.Name.Split('<')[0]),
            ReferencedTypes = c.GetReferencedTypes(allTypeNames)
        }).ToList();

        var reachable = ReachabilityAnalyzer.FindReachable(typeNodes, additionalEdges, StringComparer.OrdinalIgnoreCase);

        return allClasses
            .Where(c => reachable.Contains(c.Name.Split('<')[0]) && (c.Methods?.Any() ?? false))
            .GroupBy(c => c.Name.Split('<')[0], StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }
}
