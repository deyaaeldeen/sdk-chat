// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using ApiExtractor.Contracts;

namespace ApiExtractor.Java;

/// <summary>
/// Analyzes Java code to extract which API operations are being used.
/// Uses JavaParser (via JBang) for accurate AST-based parsing.
/// </summary>
public class JavaUsageAnalyzer : IUsageAnalyzer<ApiIndex>
{
    private static readonly string[] JBangCandidates = { "jbang" };

    private ExtractorAvailabilityResult? _availability;

    /// <inheritdoc />
    public string Language => "java";

    /// <summary>
    /// Checks if JBang is available to run the usage analyzer.
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
    public string Format(UsageIndex index)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"Analyzed {index.FileCount} files.");
        sb.AppendLine();

        if (index.CoveredOperations.Count > 0)
        {
            sb.AppendLine("COVERED OPERATIONS (already have examples):");
            foreach (var op in index.CoveredOperations.OrderBy(o => o.ClientType).ThenBy(o => o.Operation))
            {
                sb.AppendLine($"  - {op.ClientType}.{op.Operation} ({op.File}:{op.Line})");
            }
            sb.AppendLine();
        }

        if (index.UncoveredOperations.Count > 0)
        {
            sb.AppendLine("UNCOVERED OPERATIONS (need examples):");
            foreach (var op in index.UncoveredOperations.OrderBy(o => o.ClientType).ThenBy(o => o.Operation))
            {
                sb.AppendLine($"  - {op.ClientType}.{op.Operation}: {op.Signature}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

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

            return result.Success ? result.StandardOutput : null;
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

        return runtimeResult.Success ? runtimeResult.StandardOutput : null;
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

        var interfaceImplementers = new Dictionary<string, List<ClassInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var cls in allClasses)
        {
            foreach (var iface in cls.Implements ?? [])
            {
                var ifaceName = iface.Split('<')[0];
                if (!interfaceImplementers.TryGetValue(ifaceName, out var list))
                {
                    list = [];
                    interfaceImplementers[ifaceName] = list;
                }
                list.Add(cls);
            }
        }

        var references = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var cls in allClasses)
        {
            var name = cls.Name.Split('<')[0];
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
                operationTypes.Add(cls.Name.Split('<')[0]);
            }
        }

        var rootClasses = concreteClasses
            .Where(cls =>
            {
                var name = cls.Name.Split('<')[0];
                var hasOperations = cls.Methods?.Any() ?? false;
                var referencesOperations = references.TryGetValue(name, out var refs) && refs.Any(operationTypes.Contains);
                var isReferenced = referencedBy.ContainsKey(name);
                return !isReferenced && (hasOperations || referencesOperations);
            })
            .ToList();

        if (rootClasses.Count == 0)
        {
            rootClasses = concreteClasses
                .Where(cls =>
                {
                    var name = cls.Name.Split('<')[0];
                    var hasOperations = cls.Methods?.Any() ?? false;
                    var referencesOperations = references.TryGetValue(name, out var refs) && refs.Any(operationTypes.Contains);
                    return hasOperations || referencesOperations;
                })
                .ToList();
        }

        var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();

        foreach (var client in rootClasses)
        {
            var name = client.Name.Split('<')[0];
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

            if (interfaceNames.Contains(current) && interfaceImplementers.TryGetValue(current, out var implementers))
            {
                foreach (var impl in implementers)
                {
                    var implName = impl.Name.Split('<')[0];
                    if (reachable.Add(implName))
                    {
                        queue.Enqueue(implName);
                    }
                }
            }
        }

        return allClasses
            .Where(c => reachable.Contains(c.Name.Split('<')[0]) && (c.Methods?.Any() ?? false))
            .GroupBy(c => c.Name.Split('<')[0], StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }
}
