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
    private readonly ExtractorAvailabilityProvider _availability = new(TypeScriptApiExtractor.SharedConfig);

    /// <inheritdoc />
    public string Language => "typescript";

    /// <summary>
    /// Checks if Node.js is available to run the usage analyzer.
    /// </summary>
    public bool IsAvailable() => _availability.IsAvailable;

    /// <inheritdoc />
    public string? UnavailableReason => _availability.UnavailableReason;

    /// <inheritdoc />
    public async Task<UsageIndex> AnalyzeAsync(string codePath, ApiIndex apiIndex, CancellationToken ct = default)
    {
        var normalizedPath = Path.GetFullPath(codePath);

        if (!Directory.Exists(normalizedPath))
            return new UsageIndex { FileCount = 0 };

        using var activity = ExtractorTelemetry.StartUsageAnalysis(Language, normalizedPath);

        if (!HasReachableOperations(apiIndex))
            return new UsageIndex { FileCount = 0 };

        var availability = _availability.GetAvailability();
        if (!availability.IsAvailable)
            return new UsageIndex { FileCount = 0 };

        var apiJson = JsonSerializer.Serialize(apiIndex, SourceGenerationContext.Default.ApiIndex);
        var scriptDir = AppContext.BaseDirectory;

        // In RuntimeInterpreter mode, ensure npm dependencies are installed
        // before invoking the script (mirrors TypeScriptApiExtractor.RunExtractorAsync).
        if (availability.Mode == ExtractorMode.RuntimeInterpreter)
        {
            await TypeScriptApiExtractor.EnsureDependenciesAsync(scriptDir, ct).ConfigureAwait(false);
        }

        var analysisResult = await ScriptUsageAnalyzerHelper.AnalyzeAsync(new ScriptUsageAnalyzerHelper.ScriptInvocationConfig
        {
            Language = Language,
            Availability = availability,
            ApiJson = apiJson,
            SamplesPath = normalizedPath,
            BuildArgs = (avail, samplesPath) => avail.Mode switch
            {
                ExtractorMode.RuntimeInterpreter => new(
                    [Path.Combine(scriptDir, "dist", "extract_api.js"), "--usage", "-", samplesPath],
                    WorkingDirectory: scriptDir),
                _ => new(["--usage", "-", samplesPath])
            },
            SignatureLookup = BuildSignatureLookup(apiIndex),
            DeprecationLookup = BuildDeprecationLookup(apiIndex)
        }, ct).ConfigureAwait(false);

        if (analysisResult.Errors.Count > 0)
        {
            var errorMsg = string.Join("; ", analysisResult.Errors);
            ExtractorTelemetry.RecordResult(activity, false, error: errorMsg);
            return analysisResult.Index ?? new UsageIndex { FileCount = 0 };
        }

        ExtractorTelemetry.RecordResult(activity, true, analysisResult.Index?.CoveredOperations.Count ?? 0);
        return analysisResult.Index ?? new UsageIndex { FileCount = 0 };
    }

    /// <inheritdoc />
    public string Format(UsageIndex index) => UsageFormatter.Format(index);

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

    /// <summary>
    /// Builds a lookup from "TypeName.MethodName" → "MethodName(Sig)" using the API index,
    /// so uncovered operations get real signatures when the script fails to provide one.
    /// </summary>
    internal static Dictionary<string, string> BuildSignatureLookup(ApiIndex apiIndex)
    {
        var lookup = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var module in apiIndex.Modules)
        {
            foreach (var cls in module.Classes ?? [])
                foreach (var method in cls.Methods ?? [])
                    lookup.TryAdd($"{cls.Name}.{method.Name}", $"{method.Name}{method.Sig}");

            foreach (var iface in module.Interfaces ?? [])
                foreach (var method in iface.Methods ?? [])
                    lookup.TryAdd($"{iface.Name}.{method.Name}", $"{method.Name}{method.Sig}");
        }
        return lookup;
    }

    internal static HashSet<string> BuildDeprecationLookup(ApiIndex apiIndex)
    {
        var lookup = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in apiIndex.Modules)
        {
            foreach (var cls in module.Classes ?? [])
                foreach (var method in cls.Methods ?? [])
                    if (method.IsDeprecated == true || cls.IsDeprecated == true)
                        lookup.Add($"{cls.Name}.{method.Name}");

            foreach (var iface in module.Interfaces ?? [])
                foreach (var method in iface.Methods ?? [])
                    if (method.IsDeprecated == true || iface.IsDeprecated == true)
                        lookup.Add($"{iface.Name}.{method.Name}");
        }
        return lookup;
    }
}
