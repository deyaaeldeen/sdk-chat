// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text;
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

            if (result == null)
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

        var psi = new ProcessStartInfo
        {
            FileName = availability.ExecutablePath!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = scriptDir
        };
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add("--usage");
        psi.ArgumentList.Add(apiJsonPath);
        psi.ArgumentList.Add(samplesPath);

        using var process = Process.Start(psi);
        if (process == null)
            return null;

        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return process.ExitCode == 0 ? output : null;
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

        var interfaceImplementers = new Dictionary<string, List<ClassInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var cls in allClasses)
        {
            foreach (var iface in cls.Implements ?? [])
            {
                var ifaceName = iface.Split('<')[0];
                if (!interfaceImplementers.TryGetValue(ifaceName, out var list))
                {
                    list = new List<ClassInfo>();
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

        foreach (var iface in allInterfaces)
        {
            var name = iface.Name.Split('<')[0];
            references[name] = GetReferencedTypes(iface, allTypeNames);
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

        foreach (var iface in allInterfaces)
        {
            if (iface.Methods?.Any() ?? false)
            {
                operationTypes.Add(iface.Name.Split('<')[0]);
            }
        }

        var rootClasses = classesByName
            .Values
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
            rootClasses = classesByName
                .Values
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

        foreach (var root in rootClasses)
        {
            var name = root.Name.Split('<')[0];
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

            if (interfaceImplementers.TryGetValue(current, out var implementers))
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

        return reachable;
    }

    private static HashSet<string> GetReferencedTypes(InterfaceInfo iface, HashSet<string> allTypeNames)
    {
        var refs = new HashSet<string>();

        if (!string.IsNullOrEmpty(iface.Extends))
        {
            var bases = iface.Extends.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var baseEntry in bases)
            {
                var baseName = baseEntry.Trim().Split('<')[0];
                if (allTypeNames.Contains(baseName))
                {
                    refs.Add(baseName);
                }
            }
        }

        foreach (var method in iface.Methods ?? [])
        {
            foreach (var typeName in allTypeNames)
            {
                if (method.Sig.Contains(typeName) || (method.Ret?.Contains(typeName) ?? false))
                {
                    refs.Add(typeName);
                }
            }
        }

        foreach (var prop in iface.Properties ?? [])
        {
            foreach (var typeName in allTypeNames)
            {
                if (prop.Type.Contains(typeName))
                {
                    refs.Add(typeName);
                }
            }
        }

        return refs;
    }
}
