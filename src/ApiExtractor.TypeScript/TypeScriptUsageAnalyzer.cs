// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using ApiExtractor.Contracts;

namespace ApiExtractor.TypeScript;

/// <summary>
/// Analyzes TypeScript/JavaScript code to extract which API operations are being used.
/// Uses ts-morph for accurate AST-based parsing via extract_api.js --usage mode.
/// </summary>
public class TypeScriptUsageAnalyzer : IUsageAnalyzer<ApiIndex>
{
    private static readonly string[] NodeCandidates = { "node" };
    private ExtractorAvailabilityResult? _availability;

    /// <inheritdoc />
    public string Language => "typescript";

    /// <summary>
    /// Checks if Node.js is available to run the usage analyzer.
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

    private ExtractorAvailabilityResult GetAvailability()
    {
        return _availability ??= ExtractorAvailability.Check(
            language: "typescript",
            nativeBinaryName: "ts_extractor",
            runtimeToolName: "node",
            runtimeCandidates: NodeCandidates,
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

            if (!result.Success)
                return null;

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
                    ? $"TypeScript usage analyzer: Docker timed out after {ExtractorTimeout.Value.TotalSeconds}s"
                    : $"TypeScript usage analyzer: Docker failed: {dockerResult.StandardError}");
                return null;
            }

            return dockerResult.StandardOutput;
        }

        if (availability.Mode != ExtractorMode.RuntimeInterpreter)
            return null;

        var scriptDir = GetScriptDir();
        var scriptPath = Path.Combine(scriptDir, "dist", "extract_api.js");
        if (!File.Exists(scriptPath))
        {
            scriptPath = Path.Combine(scriptDir, "extract_api.mjs");
        }

        // Route through ProcessSandbox for timeout enforcement, output size limits,
        // and hardened execution — consistent with all other extractors
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

    private static bool HasReachableOperations(ApiIndex apiIndex)
    {
        var reachable = GetReachableTypeNames(
            apiIndex,
            out var classesByName,
            out var interfacesByName);

        if (classesByName.Values.Any(c => reachable.Contains(c.Name.Split('<')[0]) && (c.Methods?.Any() ?? false)))
        {
            return true;
        }

        return interfacesByName.Values.Any(i => reachable.Contains(i.Name.Split('<')[0]) && (i.Methods?.Any() ?? false));
    }

    private static HashSet<string> GetReachableTypeNames(
        ApiIndex apiIndex,
        out Dictionary<string, ClassInfo> classesByName,
        out Dictionary<string, InterfaceInfo> interfacesByName)
    {
        var allClasses = apiIndex.GetAllClasses().ToList();
        var allInterfaces = apiIndex.Modules.SelectMany(m => m.Interfaces ?? []).ToList();

        classesByName = new Dictionary<string, ClassInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var cls in allClasses)
        {
            var name = cls.Name.Split('<')[0];
            classesByName.TryAdd(name, cls);
        }

        interfacesByName = new Dictionary<string, InterfaceInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var iface in allInterfaces)
        {
            var name = iface.Name.Split('<')[0];
            interfacesByName.TryAdd(name, iface);
        }

        var allTypeNames = classesByName.Keys
            .Concat(interfacesByName.Keys)
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

        // Build type nodes: classes are root candidates, interfaces are not
        var typeNodes = new List<ReachabilityAnalyzer.TypeNode>();

        foreach (var cls in allClasses)
        {
            var name = cls.Name.Split('<')[0];
            typeNodes.Add(new ReachabilityAnalyzer.TypeNode
            {
                Name = name,
                HasOperations = cls.Methods?.Any() ?? false,
                IsExplicitEntryPoint = cls.EntryPoint == true,
                IsRootCandidate = true,
                ReferencedTypes = cls.GetReferencedTypes(allTypeNames)
            });
        }

        foreach (var iface in allInterfaces)
        {
            var name = iface.Name.Split('<')[0];
            typeNodes.Add(new ReachabilityAnalyzer.TypeNode
            {
                Name = name,
                HasOperations = iface.Methods?.Any() ?? false,
                IsExplicitEntryPoint = iface.EntryPoint == true,
                IsRootCandidate = false,
                ReferencedTypes = GetReferencedTypes(iface, allTypeNames)
            });
        }

        return ReachabilityAnalyzer.FindReachable(typeNodes, additionalEdges, StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> GetReferencedTypes(InterfaceInfo iface, HashSet<string> allTypeNames)
    {
        HashSet<string> tokens = [];

        if (!string.IsNullOrEmpty(iface.Extends))
        {
            var bases = iface.Extends.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var baseEntry in bases)
            {
                var baseName = baseEntry.Trim().Split('<')[0];
                if (allTypeNames.Contains(baseName))
                {
                    tokens.Add(baseName);
                }
            }
        }

        foreach (var method in iface.Methods ?? [])
        {
            SignatureTokenizer.TokenizeInto(method.Sig, tokens);
            SignatureTokenizer.TokenizeInto(method.Ret, tokens);
        }

        foreach (var prop in iface.Properties ?? [])
        {
            SignatureTokenizer.TokenizeInto(prop.Type, tokens);
        }

        tokens.IntersectWith(allTypeNames);
        return tokens;
    }
}
