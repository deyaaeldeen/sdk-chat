// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using PublicApiGraphEngine.Contracts;

namespace PublicApiGraphEngine.Python;

/// <summary>
/// Graphs public API surface from Python source files.
/// Shells out to Python's ast module for proper parsing.
/// </summary>
public class PythonPublicApiGraphEngine : IPublicApiGraphEngine<ApiIndex>
{
    private static readonly string ScriptPath = Path.Combine(
        AppContext.BaseDirectory,
        "graph_api.py");

    /// <summary>Shared availability configuration for all Python engine components.</summary>
    internal static readonly EngineConfig SharedConfig = new()
    {
        Language = "python",
        NativeBinaryName = "python_engine",
        RuntimeToolName = "python",
        RuntimeCandidates = ["python3", "python"]
    };

    private readonly EngineAvailabilityProvider _availability = new(SharedConfig);

    /// <inheritdoc />
    public string Language => "python";

    /// <summary>
    /// Warning message from tool resolution (if any).
    /// Check this after calling IsAvailable() for non-fatal issues.
    /// </summary>
    public string? Warning => _availability.Warning;

    /// <summary>
    /// Gets the current execution mode (NativeBinary or RuntimeInterpreter).
    /// </summary>
    public EngineMode Mode => _availability.Mode;

    /// <inheritdoc />
    public bool IsAvailable() => _availability.IsAvailable;

    /// <inheritdoc />
    public string? UnavailableReason => _availability.UnavailableReason;

    /// <inheritdoc />
    public string ToJson(ApiIndex index, bool pretty = false)
        => pretty
            ? JsonSerializer.Serialize(index, ApiIndexContext.Indented.ApiIndex)
            : JsonSerializer.Serialize(index, ApiIndexContext.Default.ApiIndex);

    /// <inheritdoc />
    public string ToStubs(ApiIndex index) => PythonFormatter.Format(index);

    /// <inheritdoc />
    async Task<EngineResult<ApiIndex>> IPublicApiGraphEngine<ApiIndex>.GraphAsync(string rootPath, CrossLanguageMap? crossLanguageMap, CancellationToken ct)
    {
        if (!IsAvailable())
            return EngineResult<ApiIndex>.CreateFailure(UnavailableReason ?? "Python not available");

        try
        {
            var (index, diagnostics) = await ExtractCoreAsync(rootPath, crossLanguageMap, ct).ConfigureAwait(false);
            return EngineResult<ApiIndex>.CreateSuccess(index, diagnostics);
        }
        catch (Exception ex)
        {
            return EngineResult<ApiIndex>.CreateFailure($"{ex.Message}\n{ex.StackTrace}");
        }
    }

    public Task<ApiIndex> GraphAsync(string rootPath, CancellationToken ct = default)
        => GraphAsync(rootPath, null, ct);

    public async Task<ApiIndex> GraphAsync(string rootPath, CrossLanguageMap? crossLanguageMap, CancellationToken ct = default)
    {
        var (index, _) = await ExtractCoreAsync(rootPath, crossLanguageMap, ct).ConfigureAwait(false);
        return index;
    }

    /// <summary>
    /// Shared engine logic that returns both the API index and any stderr warnings.
    /// </summary>
    private async Task<(ApiIndex Index, IReadOnlyList<ApiDiagnostic> Diagnostics)> ExtractCoreAsync(string rootPath, CrossLanguageMap? crossLanguageMap, CancellationToken ct)
    {
        rootPath = ProcessSandbox.ValidateRootPath(rootPath);
        using var activity = EngineTelemetry.StartGraphing(Language, rootPath);
        var startTime = System.Diagnostics.Stopwatch.GetTimestamp();

        var availability = _availability.GetAvailability();

        // Docker mode: fall back to buffered engine call
        if (availability.Mode == EngineMode.Docker)
        {
            var result = await DockerSandbox.ExecuteAsync(
                availability.DockerImageName!,
                rootPath,
                [rootPath, "--json"],
                cancellationToken: ct).ConfigureAwait(false);

            if (!result.Success)
            {
                var errorMsg = result.TimedOut
                    ? $"Python engine (docker) timed out after {EngineTimeout.Value.TotalSeconds}s"
                    : $"Python engine (docker) failed: {result.StandardError}";
                EngineTelemetry.RecordResult(activity, false, error: errorMsg);
                throw new InvalidOperationException(errorMsg);
            }

            if (result.OutputTruncated)
                throw new InvalidOperationException(
                    "Python engine output was truncated (exceeded output size limit). " +
                    "The target package may be too large for engine processing.");

            var raw = DeserializeRaw(result.StandardOutput)
                ?? throw new InvalidOperationException("Failed to parse Python engine output");

            var apiIndex = ConvertToApiIndex(raw);
            EngineTelemetry.RecordResult(activity, true, apiIndex.Modules.Count);
            var stderrDiag = ParseStderrDiagnostics(result.StandardError);
            var fin = FinalizeIndex(apiIndex, crossLanguageMap, stderrDiag);
            return (fin, fin.Diagnostics ?? []);
        }

        // Native/Runtime mode: stream stdout directly to JSON deserializer
        await using var streamResult = await RunEngineStreamAsync(rootPath, availability, ct).ConfigureAwait(false);

        if (streamResult.StandardOutputStream is null)
        {
            var errorMsg = streamResult.StartupError ?? "Python engine failed to start";
            EngineTelemetry.RecordResult(activity, false, error: errorMsg);
            throw new InvalidOperationException(errorMsg);
        }

        var rawIndex = await JsonSerializer.DeserializeAsync(
            streamResult.StandardOutputStream,
            RawPythonJsonContext.Default.RawPythonApiIndex,
            ct).ConfigureAwait(false);

        await streamResult.CompleteAsync().ConfigureAwait(false);
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startTime);

        if (!streamResult.Success)
        {
            var errorMsg = streamResult.TimedOut
                ? $"Python engine timed out after {EngineTimeout.Value.TotalSeconds}s"
                : $"Python engine failed: {streamResult.StandardError}";
            EngineTelemetry.RecordResult(activity, false, error: errorMsg);
            throw new InvalidOperationException(errorMsg);
        }

        if (rawIndex is null)
            throw new InvalidOperationException("Failed to parse Python engine output");

        var index = ConvertToApiIndex(rawIndex);
        EngineTelemetry.RecordResult(activity, true, index.Modules.Count);
        var stderrDiagnostics = ParseStderrDiagnostics(streamResult.StandardError);
        var finalized = FinalizeIndex(index, crossLanguageMap, stderrDiagnostics);
        return (finalized, finalized.Diagnostics ?? []);
    }

    /// <summary>
    /// Runs the engine with streaming stdout for JSON deserialization.
    /// </summary>
    private static async Task<StreamingProcessResult> RunEngineStreamAsync(string rootPath, EngineAvailabilityResult availability, CancellationToken ct)
    {
        if (availability.Mode == EngineMode.NativeBinary)
        {
            return await ProcessSandbox.ExecuteWithStreamAsync(
                availability.ExecutablePath!,
                [rootPath, "--json"],
                cancellationToken: ct).ConfigureAwait(false);
        }

        if (availability.Mode != EngineMode.RuntimeInterpreter)
            throw new InvalidOperationException(availability.UnavailableReason ?? "Python engine not available");

        var scriptPath = GetScriptPath();

        return await ProcessSandbox.ExecuteWithStreamAsync(
            availability.ExecutablePath!,
            [scriptPath, rootPath, "--json"],
            cancellationToken: ct).ConfigureAwait(false);
    }

    private static ApiIndex FinalizeIndex(ApiIndex index, CrossLanguageMap? crossLanguageMap, IReadOnlyList<ApiDiagnostic> upstreamDiagnostics)
    {
        var finalized = FinalizeTree(index, crossLanguageMap);
        var diagnostics = ApiDiagnosticsPostProcessor.Build(finalized, upstreamDiagnostics);
        return finalized with { Diagnostics = diagnostics };
    }

    /// <summary>
    /// Single-pass tree finalization: assigns deterministic IDs and applies cross-language mapping
    /// in one traversal instead of two separate cloning passes.
    /// </summary>
    private static ApiIndex FinalizeTree(ApiIndex index, CrossLanguageMap? map)
    {
        return index with
        {
            CrossLanguagePackageId = map?.PackageId,
            Modules = index.Modules.Select(module => FinalizeModule(module, module.Name, map)).ToList(),
            Dependencies = index.Dependencies?.Select(dependency => dependency with
            {
                Classes = dependency.Classes?.Select(cls => FinalizeClass(cls, dependency.Package, map)).ToList(),
                Functions = dependency.Functions?.Select(function =>
                {
                    var funcId = function.Id ?? BuildTypeId(dependency.Package, function.Name);
                    return function with
                    {
                        Id = funcId,
                        CrossLanguageId = map is not null && map.Ids.TryGetValue(funcId, out var xId) ? xId : null,
                    };
                }).ToList(),
            }).ToList(),
        };

        static ModuleInfo FinalizeModule(ModuleInfo module, string moduleName, CrossLanguageMap? map)
            => module with
            {
                Classes = module.Classes?.Select(cls => FinalizeClass(cls, moduleName, map)).ToList(),
                Functions = module.Functions?.Select(function =>
                {
                    var funcId = function.Id ?? BuildTypeId(moduleName, function.Name);
                    return function with
                    {
                        Id = funcId,
                        CrossLanguageId = map is not null && map.Ids.TryGetValue(funcId, out var xId) ? xId : null,
                    };
                }).ToList(),
            };

        static ClassInfo FinalizeClass(ClassInfo cls, string? scope, CrossLanguageMap? map)
        {
            var typeId = cls.Id ?? BuildTypeId(scope, cls.Name);
            return cls with
            {
                Id = typeId,
                CrossLanguageId = map is not null && map.Ids.TryGetValue(typeId, out var clsXId) ? clsXId : null,
                Methods = cls.Methods?.Select(method =>
                {
                    var methodId = method.Id ?? BuildMemberId(typeId, method.Name);
                    return method with
                    {
                        Id = methodId,
                        CrossLanguageId = map is not null && map.Ids.TryGetValue(methodId, out var methodXId) ? methodXId : null,
                    };
                }).ToList(),
                Properties = cls.Properties?.Select(property =>
                {
                    var propId = property.Id ?? BuildMemberId(typeId, property.Name);
                    return property with
                    {
                        Id = propId,
                        CrossLanguageId = map is not null && map.Ids.TryGetValue(propId, out var propXId) ? propXId : null,
                    };
                }).ToList(),
            };
        }
    }

    private static string BuildTypeId(string? moduleName, string typeName)
        => string.IsNullOrWhiteSpace(moduleName) ? typeName : $"{moduleName}.{typeName}";

    private static string BuildMemberId(string typeId, string memberName)
        => $"{typeId}.{memberName}";

    private static IReadOnlyList<ApiDiagnostic> ParseStderrDiagnostics(string? stderr)
        => string.IsNullOrWhiteSpace(stderr)
            ? []
            : stderr
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(text => new ApiDiagnostic
                {
                    Id = "SDKWARN",
                    Text = text,
                    Level = DiagnosticLevel.Warning,
                })
                .ToList();

    private static string GetScriptPath()
    {
        // SECURITY: Only load scripts from assembly directory - no path traversal allowed
        if (File.Exists(ScriptPath))
            return ScriptPath;

        throw new FileNotFoundException(
            $"Corrupt installation: graph_api.py not found at {ScriptPath}. " +
            "Reinstall the application to resolve this issue.");
    }

    private static ApiIndex ConvertToApiIndex(RawPythonApiIndex raw)
    {
        static IReadOnlyList<ParameterInfo>? MapParameters(IReadOnlyList<RawPythonParameter>? parameters)
            => parameters?.Select(p => new ParameterInfo
            {
                Name = p.Name ?? "",
                Type = p.Type,
                Default = p.Default,
                Kind = p.Kind,
            }).ToList();

        var modules = raw.Modules?.Select(m => new ModuleInfo(
            m.Name ?? "",
            m.Classes?.Select(c => new ClassInfo
            {
                Name = c.Name ?? "",
                EntryPoint = c.EntryPoint,
                ReExportedFrom = c.ReExportedFrom,
                Base = c.Base,
                Doc = c.Doc,
                IsDeprecated = c.Deprecated,
                DeprecatedMessage = c.DeprecatedMsg,
                Methods = c.Methods?.Select(mt => new MethodInfo
                {
                    Name = mt.Name ?? "",
                    Signature = mt.Sig ?? "",
                    Params = MapParameters(mt.Params),
                    Doc = mt.Doc,
                    IsAsync = mt.Async,
                    IsClassMethod = mt.Classmethod,
                    IsStaticMethod = mt.Staticmethod,
                    Ret = mt.Ret,
                    IsDeprecated = mt.Deprecated,
                    DeprecatedMessage = mt.DeprecatedMsg,
                }).ToList(),
                Properties = c.Properties?.Select(p => new PropertyInfo
                {
                    Name = p.Name ?? "",
                    Type = p.Type,
                    Doc = p.Doc,
                    IsDeprecated = p.Deprecated,
                    DeprecatedMessage = p.DeprecatedMsg,
                }).ToList()
            }).ToList(),
            m.Functions?.Select(f => new FunctionInfo
            {
                Name = f.Name ?? "",
                EntryPoint = f.EntryPoint,
                ReExportedFrom = f.ReExportedFrom,
                Signature = f.Sig ?? "",
                Params = MapParameters(f.Params),
                Doc = f.Doc,
                Ret = f.Ret,
                IsAsync = f.Async,
                IsDeprecated = f.Deprecated,
                DeprecatedMessage = f.DeprecatedMsg,
            }).ToList()
        )).ToList() ?? [];

        var dependencies = raw.Dependencies?.Select(d => new DependencyInfo
        {
            Package = d.Package ?? "",
            Classes = d.Classes?.Select(c => new ClassInfo
            {
                Name = c.Name ?? "",
                Base = c.Base,
                Doc = c.Doc,
                IsDeprecated = c.Deprecated,
                DeprecatedMessage = c.DeprecatedMsg,
                Methods = c.Methods?.Select(mt => new MethodInfo
                {
                    Name = mt.Name ?? "",
                    Signature = mt.Sig ?? "",
                    Params = MapParameters(mt.Params),
                    Doc = mt.Doc,
                    IsAsync = mt.Async,
                    IsClassMethod = mt.Classmethod,
                    IsStaticMethod = mt.Staticmethod,
                    Ret = mt.Ret,
                    IsDeprecated = mt.Deprecated,
                    DeprecatedMessage = mt.DeprecatedMsg,
                }).ToList(),
                Properties = c.Properties?.Select(p => new PropertyInfo
                {
                    Name = p.Name ?? "",
                    Type = p.Type,
                    Doc = p.Doc,
                    IsDeprecated = p.Deprecated,
                    DeprecatedMessage = p.DeprecatedMsg,
                }).ToList()
            }).ToList(),
            Functions = d.Functions?.Select(f => new FunctionInfo
            {
                Name = f.Name ?? "",
                Signature = f.Sig ?? "",
                Params = MapParameters(f.Params),
                Doc = f.Doc,
                Ret = f.Ret,
                IsAsync = f.Async,
                IsDeprecated = f.Deprecated,
                DeprecatedMessage = f.DeprecatedMsg,
            }).ToList()
        }).ToList();

        return new ApiIndex(raw.Package ?? "", modules, dependencies);
    }

    // AOT-safe deserialization using source-generated context
    private static RawPythonApiIndex? DeserializeRaw(string json) =>
        JsonSerializer.Deserialize(json, RawPythonJsonContext.Default.RawPythonApiIndex);
}
