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
    public string? UnavailableReason => _availability.UnavailableReason;

    /// <inheritdoc />
    public async Task<UsageIndex> AnalyzeAsync(string codePath, ApiIndex apiIndex, CancellationToken ct = default)
    {
        var normalizedPath = Path.GetFullPath(codePath);

        if (!Directory.Exists(normalizedPath))
            return new UsageIndex { FileCount = 0 };

        using var activity = ExtractorTelemetry.StartUsageAnalysis(Language, normalizedPath);

        var clientClasses = GetClientAndSubclientClasses(apiIndex);

        // Get client classes from API - if none, no point analyzing
        if (!clientClasses.Any())
            return new UsageIndex { FileCount = 0 };

        var availability = _availability.GetAvailability();
        if (!availability.IsAvailable)
            return new UsageIndex { FileCount = 0 };

        var apiJson = JsonSerializer.Serialize(apiIndex, SourceGenerationContext.Default.ApiIndex);
        var scriptDir = AppContext.BaseDirectory;

        var analysisResult = await ScriptUsageAnalyzerHelper.AnalyzeAsync(new ScriptUsageAnalyzerHelper.ScriptInvocationConfig
        {
            Language = Language,
            Availability = availability,
            ApiJson = apiJson,
            SamplesPath = normalizedPath,
            BuildArgs = (avail, samplesPath) => avail.Mode switch
            {
                ExtractorMode.RuntimeInterpreter => new(
                    [Path.Combine(scriptDir, "ExtractApi.java"), "--usage", "-", samplesPath],
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
            IsExplicitEntryPoint = c.EntryPoint == true,
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

    /// <summary>
    /// Builds a lookup from "TypeName.MethodName" → "ReturnType MethodName(Sig)" using the API index,
    /// so uncovered operations get real signatures when the script fails to provide one.
    /// </summary>
    internal static Dictionary<string, string> BuildSignatureLookup(ApiIndex apiIndex)
    {
        var lookup = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pkg in apiIndex.Packages)
        {
            foreach (var cls in pkg.Classes ?? [])
                foreach (var method in cls.Methods ?? [])
                {
                    var ret = !string.IsNullOrEmpty(method.Ret) ? $"{method.Ret} " : "";
                    lookup.TryAdd($"{cls.Name}.{method.Name}", $"{ret}{method.Name}{method.Sig}");
                }

            foreach (var iface in pkg.Interfaces ?? [])
                foreach (var method in iface.Methods ?? [])
                {
                    var ret = !string.IsNullOrEmpty(method.Ret) ? $"{method.Ret} " : "";
                    lookup.TryAdd($"{iface.Name}.{method.Name}", $"{ret}{method.Name}{method.Sig}");
                }
        }
        return lookup;
    }

    internal static HashSet<string> BuildDeprecationLookup(ApiIndex apiIndex)
    {
        var lookup = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cls in apiIndex.GetAllTypes())
            foreach (var method in cls.Methods ?? [])
                if (method.IsDeprecated == true || cls.IsDeprecated == true)
                    lookup.Add($"{cls.Name}.{method.Name}");
        return lookup;
    }
}
