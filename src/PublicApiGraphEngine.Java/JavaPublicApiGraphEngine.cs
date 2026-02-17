// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using PublicApiGraphEngine.Contracts;

namespace PublicApiGraphEngine.Java;

/// <summary>
/// Graphs public API surface from Java packages using JBang + JavaParser.
/// </summary>
public class JavaPublicApiGraphEngine : IPublicApiGraphEngine<ApiIndex>
{
    /// <summary>Shared availability configuration for all Java engine components.</summary>
    internal static readonly EngineConfig SharedConfig = new()
    {
        Language = "java",
        NativeBinaryName = "java_engine",
        RuntimeToolName = "jbang",
        RuntimeCandidates = ["jbang"]
    };

    private readonly EngineAvailabilityProvider _availability = new(SharedConfig);

    /// <inheritdoc />
    public string Language => "java";

    /// <summary>
    /// Warning message from tool resolution (if any).
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
            ? JsonSerializer.Serialize(index, SourceGenerationContext.Indented.ApiIndex)
            : JsonSerializer.Serialize(index, SourceGenerationContext.Default.ApiIndex);

    /// <inheritdoc />
    public string ToStubs(ApiIndex index) => JavaFormatter.Format(index);

    /// <inheritdoc />
    async Task<EngineResult<ApiIndex>> IPublicApiGraphEngine<ApiIndex>.GraphAsync(string rootPath, CrossLanguageMap? crossLanguageMap, CancellationToken ct)
    {
        if (!IsAvailable())
            return EngineResult<ApiIndex>.CreateFailure(UnavailableReason ?? "JBang not available");

        using var activity = EngineTelemetry.StartGraphing(Language, rootPath);
        try
        {
            var (result, diagnostics) = await ExtractCoreAsync(rootPath, crossLanguageMap, ct).ConfigureAwait(false);
            if (result is not null)
            {
                EngineTelemetry.RecordResult(activity, true, result.Packages.Count);
                return EngineResult<ApiIndex>.CreateSuccess(result, diagnostics);
            }
            EngineTelemetry.RecordResult(activity, false, error: "No API surface graphed");
            return EngineResult<ApiIndex>.CreateFailure("No API surface graphed");
        }
        catch (Exception ex)
        {
            EngineTelemetry.RecordResult(activity, false, error: ex.Message);
            return EngineResult<ApiIndex>.CreateFailure($"{ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>
    /// Graph API from a Java package directory.
    /// Prefers pre-compiled binary from build, falls back to JBang runtime.
    /// </summary>
    public Task<ApiIndex> GraphAsync(string rootPath, CancellationToken ct = default)
        => GraphAsync(rootPath, null, ct);

    public async Task<ApiIndex> GraphAsync(string rootPath, CrossLanguageMap? crossLanguageMap, CancellationToken ct = default)
    {
        var (index, _) = await ExtractCoreAsync(rootPath, crossLanguageMap, ct).ConfigureAwait(false);
        return index ?? throw new InvalidOperationException("Java engine returned no API surface.");
    }

    /// <summary>
    /// Shared engine logic that returns both the API index and any stderr warnings.
    /// </summary>
    private async Task<(ApiIndex? Index, IReadOnlyList<ApiDiagnostic> Diagnostics)> ExtractCoreAsync(string rootPath, CrossLanguageMap? crossLanguageMap, CancellationToken ct)
    {
        rootPath = ProcessSandbox.ValidateRootPath(rootPath);
        var availability = _availability.GetAvailability();

        // Docker mode: fall back to buffered engine call
        if (availability.Mode == EngineMode.Docker)
        {
            var result = await RunEngineAsync("--json", rootPath, ct).ConfigureAwait(false);
            var diag = ParseStderrDiagnostics(result.StandardError);

            if (string.IsNullOrWhiteSpace(result.StandardOutput))
                return (null, diag);

            if (result.OutputTruncated)
                throw new InvalidOperationException(
                    "Java engine output was truncated (exceeded output size limit). " +
                    "The target package may be too large for engine processing.");

            var idx = JsonSerializer.Deserialize(result.StandardOutput, SourceGenerationContext.Default.ApiIndex);
            if (idx is null) return (null, diag);

            var fin = FinalizeIndex(idx, crossLanguageMap, diag);
            return (fin, fin.Diagnostics);
        }

        // Native/Runtime mode: stream stdout directly to JSON deserializer
        await using var streamResult = await RunEngineStreamAsync(rootPath, ct).ConfigureAwait(false);

        if (streamResult.StandardOutputStream is null)
        {
            var diagnostics = ParseStderrDiagnostics(streamResult.StandardError);
            return (null, diagnostics);
        }

        var index = await JsonSerializer.DeserializeAsync(
            streamResult.StandardOutputStream,
            SourceGenerationContext.Default.ApiIndex,
            ct).ConfigureAwait(false);

        await streamResult.CompleteAsync().ConfigureAwait(false);
        var stderrDiagnostics = ParseStderrDiagnostics(streamResult.StandardError);

        if (!streamResult.Success)
        {
            var errorMsg = streamResult.TimedOut
                ? $"Java engine timed out after {EngineTimeout.Value.TotalSeconds}s"
                : $"Java engine failed: {streamResult.StandardError}";
            throw new InvalidOperationException(errorMsg);
        }

        if (index is null) return (null, stderrDiagnostics);

        var finalized = FinalizeIndex(index, crossLanguageMap, stderrDiagnostics);
        return (finalized, finalized.Diagnostics);
    }

    /// <summary>
    /// Runs the engine with streaming stdout for JSON deserialization.
    /// </summary>
    private async Task<StreamingProcessResult> RunEngineStreamAsync(string rootPath, CancellationToken ct)
    {
        var availability = _availability.GetAvailability();

        if (availability.Mode == EngineMode.NativeBinary)
        {
            return await ProcessSandbox.ExecuteWithStreamAsync(
                availability.ExecutablePath!,
                [rootPath, "--json"],
                cancellationToken: ct).ConfigureAwait(false);
        }

        if (availability.Mode != EngineMode.RuntimeInterpreter)
            throw new InvalidOperationException(availability.UnavailableReason ?? "Java engine not available");

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
        => index with
        {
            CrossLanguagePackageId = map?.PackageId,
            Packages = index.Packages.Select(package => package with
            {
                Classes = package.Classes?.Select(cls => FinalizeClassInfo(cls, package.Name, map)).ToList(),
                Interfaces = package.Interfaces?.Select(iface => FinalizeClassInfo(iface, package.Name, map)).ToList(),
                Annotations = package.Annotations?.Select(annotation => FinalizeClassInfo(annotation, package.Name, map)).ToList(),
                Enums = package.Enums?.Select(en =>
                {
                    var enumId = en.Id ?? BuildTypeId(package.Name, en.Name);
                    return en with
                    {
                        Id = enumId,
                        CrossLanguageId = map is not null && map.Ids.TryGetValue(enumId, out var enumXId) ? enumXId : null,
                        Methods = en.Methods?.Select(method =>
                        {
                            var methodId = method.Id ?? BuildMemberId(enumId, method.Name);
                            return method with
                            {
                                Id = methodId,
                                CrossLanguageId = map is not null && map.Ids.TryGetValue(methodId, out var methodXId) ? methodXId : null,
                            };
                        }).ToList(),
                    };
                }).ToList(),
            }).ToList(),
        };

    private static ClassInfo FinalizeClassInfo(ClassInfo cls, string packageName, CrossLanguageMap? map)
    {
        var typeId = cls.Id ?? BuildTypeId(packageName, cls.Name);
        return cls with
        {
            Id = typeId,
            CrossLanguageId = map is not null && map.Ids.TryGetValue(typeId, out var clsXId) ? clsXId : null,
            Constructors = cls.Constructors?.Select(ctor =>
            {
                var ctorId = ctor.Id ?? BuildMemberId(typeId, ctor.Name);
                return ctor with { Id = ctorId, CrossLanguageId = map is not null && map.Ids.TryGetValue(ctorId, out var ctorXId) ? ctorXId : null };
            }).ToList(),
            Methods = cls.Methods?.Select(method =>
            {
                var methodId = method.Id ?? BuildMemberId(typeId, method.Name);
                return method with { Id = methodId, CrossLanguageId = map is not null && map.Ids.TryGetValue(methodId, out var methodXId) ? methodXId : null };
            }).ToList(),
            Fields = cls.Fields?.Select(field =>
            {
                var fieldId = field.Id ?? BuildMemberId(typeId, field.Name);
                return field with { Id = fieldId, CrossLanguageId = map is not null && map.Ids.TryGetValue(fieldId, out var fieldXId) ? fieldXId : null };
            }).ToList(),
        };
    }

    private static string BuildTypeId(string packageName, string typeName)
        => string.IsNullOrWhiteSpace(packageName) ? typeName : $"{packageName}.{typeName}";

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

    /// <summary>
    /// Extract and format as Java stub syntax.
    /// </summary>
    public async Task<string> ExtractAsJavaAsync(string rootPath, CancellationToken ct = default)
    {
        var result = await RunEngineAsync("--stub", rootPath, ct).ConfigureAwait(false);
        return result.StandardOutput;
    }

    /// <summary>
    /// Runs the Java engine with the given output flag, dispatching to the correct
    /// execution mode (NativeBinary, RuntimeInterpreter, or Docker).
    /// </summary>
    private async Task<ProcessResult> RunEngineAsync(string outputFlag, string rootPath, CancellationToken ct)
    {
        rootPath = ProcessSandbox.ValidateRootPath(rootPath);
        var availability = _availability.GetAvailability();

        if (availability.Mode == EngineMode.NativeBinary)
        {
            var result = await ProcessSandbox.ExecuteAsync(
                availability.ExecutablePath!,
                [rootPath, outputFlag],
                cancellationToken: ct
            ).ConfigureAwait(false);

            if (!result.Success)
            {
                var errorMsg = result.TimedOut
                    ? $"Java engine timed out after {EngineTimeout.Value.TotalSeconds}s"
                    : $"Java engine failed: {result.StandardError}";
                throw new InvalidOperationException(errorMsg);
            }

            return result;
        }

        if (availability.Mode == EngineMode.Docker)
        {
            var dockerResult = await DockerSandbox.ExecuteAsync(
                availability.DockerImageName!,
                rootPath,
                [rootPath, outputFlag],
                cancellationToken: ct
            ).ConfigureAwait(false);

            if (!dockerResult.Success)
            {
                var errorMsg = dockerResult.TimedOut
                    ? $"Java engine timed out after {EngineTimeout.Value.TotalSeconds}s"
                    : $"Java engine failed: {dockerResult.StandardError}";
                throw new InvalidOperationException(errorMsg);
            }

            return dockerResult;
        }

        if (availability.Mode != EngineMode.RuntimeInterpreter)
        {
            throw new InvalidOperationException(availability.UnavailableReason ?? "Java engine not available");
        }

        // Fall back to JBang runtime
        var scriptPath = GetScriptPath();

        var jbangResult = await ProcessSandbox.ExecuteAsync(
            availability.ExecutablePath!,
            [scriptPath, rootPath, outputFlag],
            cancellationToken: ct
        ).ConfigureAwait(false);

        if (!jbangResult.Success)
        {
            var errorMsg = jbangResult.TimedOut
                ? $"Java engine timed out after {EngineTimeout.Value.TotalSeconds}s"
                : $"JBang failed: {jbangResult.StandardError}";
            throw new InvalidOperationException(errorMsg);
        }

        return jbangResult;
    }

    private static string GetScriptPath()
    {
        // SECURITY: Only load scripts from assembly directory - no path traversal allowed
        var assemblyDir = AppContext.BaseDirectory;
        var scriptPath = Path.Combine(assemblyDir, "GraphApi.java");

        if (File.Exists(scriptPath))
            return scriptPath;

        throw new FileNotFoundException(
            $"Corrupt installation: GraphApi.java not found at {scriptPath}. " +
            "Reinstall the application to resolve this issue.");
    }
}
