// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using ApiExtractor.Contracts;

namespace ApiExtractor.Go;

/// <summary>
/// Analyzes Go code to extract which API operations are being used.
/// Uses go/ast (via go run) for accurate AST-based parsing.
/// </summary>
public class GoUsageAnalyzer : IUsageAnalyzer<ApiIndex>
{
    private static readonly string[] GoCandidates = { "go", "/usr/local/go/bin/go", "/opt/go/bin/go" };

    // Benign race: worst case two threads both compute the same availability result.
    // Reference assignment is atomic in .NET, so no corruption is possible.
    private ExtractorAvailabilityResult? _availability;

    /// <inheritdoc />
    public string Language => "go";

    /// <summary>
    /// Checks if Go is available to run the usage analyzer.
    /// </summary>
    public bool IsAvailable() => GetAvailability().IsAvailable;

    /// <inheritdoc />
    public async Task<UsageIndex> AnalyzeAsync(string codePath, ApiIndex apiIndex, CancellationToken ct = default)
    {
        var normalizedPath = Path.GetFullPath(codePath);

        if (!Directory.Exists(normalizedPath))
            return new UsageIndex { FileCount = 0 };

        if (!HasReachableOperations(apiIndex))
            return new UsageIndex { FileCount = 0 };

        var availability = GetAvailability();
        if (!availability.IsAvailable)
            return new UsageIndex { FileCount = 0 };

        var tempApiFile = Path.GetTempFileName();
        try
        {
            var apiJson = JsonSerializer.Serialize(apiIndex, SourceGenerationContext.Default.ApiIndex);
            await File.WriteAllTextAsync(tempApiFile, apiJson, ct);

            var output = await AnalyzeUsageAsync(availability, tempApiFile, normalizedPath, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(output))
                return new UsageIndex { FileCount = 0 };

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
            language: "go",
            nativeBinaryName: "go_extractor",
            runtimeToolName: "go",
            runtimeCandidates: GoCandidates,
            nativeValidationArgs: "--help",
            runtimeValidationArgs: "version");
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
                ["-usage", apiJsonPath, samplesPath],
                cancellationToken: ct
            ).ConfigureAwait(false);

            return result.Success ? result.StandardOutput : null;
        }

        if (availability.Mode == ExtractorMode.Docker)
        {
            var dockerResult = await DockerSandbox.ExecuteAsync(
                availability.DockerImageName!,
                [apiJsonPath, samplesPath],
                ["-usage", apiJsonPath, samplesPath],
                cancellationToken: ct
            ).ConfigureAwait(false);

            if (!dockerResult.Success)
            {
                Console.Error.WriteLine(dockerResult.TimedOut
                    ? $"Go usage analyzer: Docker timed out after {ExtractorTimeout.Value.TotalSeconds}s"
                    : $"Go usage analyzer: Docker failed: {dockerResult.StandardError}");
                return null;
            }

            return dockerResult.StandardOutput;
        }

        if (availability.Mode != ExtractorMode.RuntimeInterpreter)
            return null;

        var scriptDir = GetScriptDir();
        var scriptPath = Path.Combine(scriptDir, "extract_api.go");

        var runtimeResult = await ProcessSandbox.ExecuteAsync(
            availability.ExecutablePath!,
            ["run", scriptPath, "-usage", apiJsonPath, samplesPath],
            workingDirectory: scriptDir,
            cancellationToken: ct
        ).ConfigureAwait(false);

        return runtimeResult.Success ? runtimeResult.StandardOutput : null;
    }

    // AOT-safe deserialization using source-generated context
    private static UsageResult? DeserializeResult(string json) =>
        JsonSerializer.Deserialize(json, ExtractorJsonContext.Default.UsageResult);

    private static bool HasReachableOperations(ApiIndex apiIndex)
    {
        var reachable = GetReachableTypeNames(apiIndex, out var allStructs, out var allInterfaces);

        if (allStructs.Any(s => reachable.Contains(s.Name) && (s.Methods?.Any() ?? false)))
        {
            return true;
        }

        return allInterfaces.Any(i => reachable.Contains(i.Name) && (i.Methods?.Any() ?? false));
    }

    private static HashSet<string> GetReachableTypeNames(
        ApiIndex apiIndex,
        out List<StructApi> allStructs,
        out List<IfaceApi> allInterfaces)
    {
        allStructs = apiIndex.GetAllStructs().ToList();
        allInterfaces = apiIndex.Packages
            .SelectMany(p => p.Interfaces ?? [])
            .ToList();

        // Go is case-sensitive — use Ordinal comparison for type/method names.
        var allTypeNames = allStructs
            .Select(s => s.Name)
            .Concat(allInterfaces.Select(i => i.Name))
            .ToHashSet(StringComparer.Ordinal);

        var interfaceMethods = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var iface in allInterfaces)
        {
            var methods = new HashSet<string>(StringComparer.Ordinal);
            foreach (var method in iface.Methods ?? [])
            {
                methods.Add(method.Name);
            }

            if (methods.Count > 0)
            {
                interfaceMethods[iface.Name] = methods;
            }
        }

        // Structural interface matching: a struct implements an interface if it has
        // methods with matching names. This mirrors Go's structural typing model.
        // Note: checking by name only (not full signature) may produce false positives
        // if unrelated interfaces share method names, but this is rare in practice
        // and is the correct approach for Go's type system.
        var interfaceImplementers = new Dictionary<string, List<StructApi>>(StringComparer.Ordinal);
        foreach (var iface in interfaceMethods)
        {
            foreach (var strct in allStructs)
            {
                var structMethods = new HashSet<string>(StringComparer.Ordinal);
                foreach (var method in strct.Methods ?? [])
                {
                    structMethods.Add(method.Name);
                }

                if (iface.Value.All(structMethods.Contains))
                {
                    if (!interfaceImplementers.TryGetValue(iface.Key, out var list))
                    {
                        list = [];
                        interfaceImplementers[iface.Key] = list;
                    }
                    list.Add(strct);
                }
            }
        }

        var references = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var strct in allStructs)
        {
            references[strct.Name] = strct.GetReferencedTypes(allTypeNames);
        }

        foreach (var iface in allInterfaces)
        {
            references[iface.Name] = GetReferencedTypes(iface, allTypeNames);
        }

        var referencedBy = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (typeName, refs) in references)
        {
            foreach (var target in refs)
            {
                if (!string.Equals(target, typeName, StringComparison.OrdinalIgnoreCase))
                {
                    referencedBy[target] = referencedBy.TryGetValue(target, out var count) ? count + 1 : 1;
                }
            }
        }

        var operationTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var strct in allStructs)
        {
            if (strct.Methods?.Any() ?? false)
            {
                operationTypes.Add(strct.Name);
            }
        }

        foreach (var iface in allInterfaces)
        {
            if (iface.Methods?.Any() ?? false)
            {
                operationTypes.Add(iface.Name);
            }
        }

        var rootStructs = allStructs
            .Where(strct =>
            {
                var hasOperations = strct.Methods?.Any() ?? false;
                var referencesOperations = references.TryGetValue(strct.Name, out var refs) && refs.Any(operationTypes.Contains);
                var isReferenced = referencedBy.ContainsKey(strct.Name);
                return !isReferenced && (hasOperations || referencesOperations);
            })
            .ToList();

        if (rootStructs.Count == 0)
        {
            rootStructs = allStructs
                .Where(strct =>
                {
                    var hasOperations = strct.Methods?.Any() ?? false;
                    var referencesOperations = references.TryGetValue(strct.Name, out var refs) && refs.Any(operationTypes.Contains);
                    return hasOperations || referencesOperations;
                })
                .ToList();
        }

        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();

        foreach (var root in rootStructs)
        {
            if (reachable.Add(root.Name))
            {
                queue.Enqueue(root.Name);
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

            if (interfaceImplementers.TryGetValue(current, out var implementers))
            {
                foreach (var impl in implementers)
                {
                    if (reachable.Add(impl.Name))
                    {
                        queue.Enqueue(impl.Name);
                    }
                }
            }
        }

        return reachable;
    }

    private static HashSet<string> GetReferencedTypes(IfaceApi iface, HashSet<string> allTypeNames)
    {
        HashSet<string> tokens = new(StringComparer.OrdinalIgnoreCase);

        // Embedded interfaces are direct composition dependencies
        foreach (var embed in iface.Embeds ?? [])
        {
            if (allTypeNames.Contains(embed))
                tokens.Add(embed);
        }

        foreach (var method in iface.Methods ?? [])
        {
            SignatureTokenizer.TokenizeInto(method.Sig, tokens);
            SignatureTokenizer.TokenizeInto(method.Ret, tokens);
        }

        tokens.IntersectWith(allTypeNames);
        return tokens;
    }
}
