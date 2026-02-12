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
    private readonly ExtractorAvailabilityProvider _availability = new(GoApiExtractor.SharedConfig);

    /// <inheritdoc />
    public string Language => "go";

    /// <summary>
    /// Checks if Go is available to run the usage analyzer.
    /// </summary>
    public bool IsAvailable() => _availability.IsAvailable;

    /// <inheritdoc />
    public async Task<UsageIndex> AnalyzeAsync(string codePath, ApiIndex apiIndex, CancellationToken ct = default)
    {
        var normalizedPath = Path.GetFullPath(codePath);

        if (!Directory.Exists(normalizedPath))
            return new UsageIndex { FileCount = 0 };

        if (!HasReachableOperations(apiIndex))
            return new UsageIndex { FileCount = 0 };

        var availability = _availability.GetAvailability();
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

            if (!result.Success)
            {
                Console.Error.WriteLine(result.TimedOut
                    ? $"Go usage analyzer: timed out after {ExtractorTimeout.Value.TotalSeconds}s"
                    : $"Go usage analyzer: failed (exit {result.ExitCode}): {result.StandardError}");
                return null;
            }

            return result.StandardOutput;
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

        if (!runtimeResult.Success)
        {
            Console.Error.WriteLine(runtimeResult.TimedOut
                ? $"Go usage analyzer: timed out after {ExtractorTimeout.Value.TotalSeconds}s"
                : $"Go usage analyzer: failed (exit {runtimeResult.ExitCode}): {runtimeResult.StandardError}");
            return null;
        }

        return runtimeResult.StandardOutput;
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

        // Structural interface matching: a struct implements an interface if it has
        // methods with matching names. This mirrors Go's structural typing model.
        var interfaceMethods = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var iface in allInterfaces)
        {
            var methods = new HashSet<string>(StringComparer.Ordinal);
            foreach (var method in iface.Methods ?? [])
                methods.Add(method.Name);

            if (methods.Count > 0)
                interfaceMethods[iface.Name] = methods;
        }

        var additionalEdges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var iface in interfaceMethods)
        {
            foreach (var strct in allStructs)
            {
                var structMethods = new HashSet<string>(StringComparer.Ordinal);
                foreach (var method in strct.Methods ?? [])
                    structMethods.Add(method.Name);

                if (iface.Value.All(structMethods.Contains))
                {
                    if (!additionalEdges.TryGetValue(iface.Key, out var list))
                    {
                        list = [];
                        additionalEdges[iface.Key] = list;
                    }
                    list.Add(strct.Name);
                }
            }
        }

        // Build type nodes: structs are root candidates, interfaces are not
        var typeNodes = new List<ReachabilityAnalyzer.TypeNode>();

        foreach (var strct in allStructs)
        {
            typeNodes.Add(new ReachabilityAnalyzer.TypeNode
            {
                Name = strct.Name,
                HasOperations = strct.Methods?.Any() ?? false,
                IsExplicitEntryPoint = strct.EntryPoint == true,
                IsRootCandidate = true,
                ReferencedTypes = strct.GetReferencedTypes(allTypeNames)
            });
        }

        foreach (var iface in allInterfaces)
        {
            typeNodes.Add(new ReachabilityAnalyzer.TypeNode
            {
                Name = iface.Name,
                HasOperations = iface.Methods?.Any() ?? false,
                IsExplicitEntryPoint = iface.EntryPoint == true,
                IsRootCandidate = false,
                ReferencedTypes = GetReferencedTypes(iface, allTypeNames)
            });
        }

        return ReachabilityAnalyzer.FindReachable(typeNodes, additionalEdges, StringComparer.Ordinal);
    }

    private static HashSet<string> GetReferencedTypes(IfaceApi iface, HashSet<string> allTypeNames)
    {
        // Go is case-sensitive — use Ordinal, consistent with struct GetReferencedTypes
        HashSet<string> tokens = new(StringComparer.Ordinal);

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

    /// <summary>
    /// Builds a lookup from "TypeName.MethodName" → "MethodName(Sig) Ret" using the API index,
    /// so uncovered operations get real signatures when the script fails to provide one.
    /// </summary>
    internal static Dictionary<string, string> BuildSignatureLookup(ApiIndex apiIndex)
    {
        var lookup = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pkg in apiIndex.Packages ?? [])
        {
            foreach (var strct in pkg.Structs ?? [])
                foreach (var method in strct.Methods ?? [])
                {
                    var ret = !string.IsNullOrEmpty(method.Ret) ? $" {method.Ret}" : "";
                    lookup.TryAdd($"{strct.Name}.{method.Name}", $"{method.Name}({method.Sig}){ret}");
                }

            foreach (var iface in pkg.Interfaces ?? [])
                foreach (var method in iface.Methods ?? [])
                {
                    var ret = !string.IsNullOrEmpty(method.Ret) ? $" {method.Ret}" : "";
                    lookup.TryAdd($"{iface.Name}.{method.Name}", $"{method.Name}({method.Sig}){ret}");
                }
        }
        return lookup;
    }
}
