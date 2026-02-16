// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using ApiExtractor.Contracts;

namespace ApiExtractor.TypeScript;

/// <summary>
/// Extracts public API surface from TypeScript packages using ts-morph.
/// </summary>
public class TypeScriptApiExtractor : IApiExtractor<ApiIndex>
{
    private static readonly SemaphoreSlim NpmInstallLock = new(1, 1);

    /// <summary>Shared availability configuration for all TypeScript extractor components.</summary>
    internal static readonly ExtractorConfig SharedConfig = new()
    {
        Language = "typescript",
        NativeBinaryName = "ts_extractor",
        RuntimeToolName = "node",
        RuntimeCandidates = ["node"]
    };

    private readonly ExtractorAvailabilityProvider _availability = new(SharedConfig);

    /// <inheritdoc />
    public string Language => "typescript";

    /// <summary>
    /// Warning message from tool resolution (if any).
    /// </summary>
    public string? Warning => _availability.Warning;

    /// <summary>
    /// Gets the current execution mode (NativeBinary or RuntimeInterpreter).
    /// </summary>
    public ExtractorMode Mode => _availability.Mode;

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
    public string ToStubs(ApiIndex index) => TypeScriptFormatter.Format(index);

    /// <inheritdoc />
    async Task<ExtractorResult<ApiIndex>> IApiExtractor<ApiIndex>.ExtractAsync(string rootPath, CrossLanguageMap? crossLanguageMap, CancellationToken ct)
    {
        if (!IsAvailable())
            return ExtractorResult<ApiIndex>.CreateFailure(UnavailableReason ?? "Node.js not available");

        using var activity = ExtractorTelemetry.StartExtraction(Language, rootPath);
        try
        {
            var (result, diagnostics) = await ExtractCoreAsync(rootPath, crossLanguageMap, ct).ConfigureAwait(false);
            if (result is not null)
            {
                ExtractorTelemetry.RecordResult(activity, true, result.Modules.Count);
                return ExtractorResult<ApiIndex>.CreateSuccess(result, diagnostics);
            }
            ExtractorTelemetry.RecordResult(activity, false, error: "No API surface extracted");
            return ExtractorResult<ApiIndex>.CreateFailure("No API surface extracted");
        }
        catch (Exception ex)
        {
            ExtractorTelemetry.RecordResult(activity, false, error: ex.Message);
            return ExtractorResult<ApiIndex>.CreateFailure($"{ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>
    /// Extract API from a TypeScript package directory.
    /// Prefers pre-compiled binary from build, falls back to Node.js runtime.
    /// </summary>
    public Task<ApiIndex> ExtractAsync(string rootPath, CancellationToken ct = default)
        => ExtractAsync(rootPath, null, ct);

    public async Task<ApiIndex> ExtractAsync(string rootPath, CrossLanguageMap? crossLanguageMap, CancellationToken ct = default)
    {
        var (index, _) = await ExtractCoreAsync(rootPath, crossLanguageMap, ct).ConfigureAwait(false);
        return index ?? throw new InvalidOperationException("TypeScript extraction returned no API surface.");
    }

    /// <summary>
    /// Shared extraction logic that returns both the API index and any stderr warnings.
    /// </summary>
    private async Task<(ApiIndex? Index, IReadOnlyList<ApiDiagnostic> Diagnostics)> ExtractCoreAsync(string rootPath, CrossLanguageMap? crossLanguageMap, CancellationToken ct)
    {
        rootPath = ProcessSandbox.ValidateRootPath(rootPath);
        var availability = _availability.GetAvailability();

        // Docker mode: fall back to buffered extraction (Docker API returns string)
        if (availability.Mode == ExtractorMode.Docker)
        {
            var result = await RunExtractorAsync("--json", rootPath, ct).ConfigureAwait(false);
            var diag = ParseStderrDiagnostics(result.StandardError);

            if (string.IsNullOrWhiteSpace(result.StandardOutput))
                return (null, diag);

            if (result.OutputTruncated)
                throw new InvalidOperationException(
                    "TypeScript extractor output was truncated (exceeded output size limit). " +
                    "The target package may be too large for extraction.");

            var idx = JsonSerializer.Deserialize(result.StandardOutput, SourceGenerationContext.Default.ApiIndex);
            if (idx is null) return (null, diag);

            var fin = FinalizeIndex(idx, crossLanguageMap, diag);
            return (fin, fin.Diagnostics);
        }

        // Native/Runtime mode: stream stdout directly to JSON deserializer (halves peak memory)
        await using var streamResult = await RunExtractorStreamAsync(rootPath, ct).ConfigureAwait(false);

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
                ? $"TypeScript extractor timed out after {ExtractorTimeout.Value.TotalSeconds}s"
                : $"TypeScript extractor failed: {streamResult.StandardError}";
            throw new InvalidOperationException(errorMsg);
        }

        if (index is null) return (null, stderrDiagnostics);

        var finalized = FinalizeIndex(index, crossLanguageMap, stderrDiagnostics);
        return (finalized, finalized.Diagnostics);
    }

    /// <summary>
    /// Runs the extractor with streaming stdout for JSON deserialization.
    /// Used in NativeBinary and RuntimeInterpreter modes.
    /// </summary>
    private async Task<StreamingProcessResult> RunExtractorStreamAsync(string rootPath, CancellationToken ct)
    {
        var availability = _availability.GetAvailability();

        if (availability.Mode == ExtractorMode.NativeBinary)
        {
            return await ProcessSandbox.ExecuteWithStreamAsync(
                availability.ExecutablePath!,
                [rootPath, "--json"],
                cancellationToken: ct).ConfigureAwait(false);
        }

        if (availability.Mode != ExtractorMode.RuntimeInterpreter)
        {
            throw new InvalidOperationException(availability.UnavailableReason ?? "TypeScript extractor not available");
        }

        var scriptDir = GetScriptDir();
        await EnsureDependenciesAsync(scriptDir, ct).ConfigureAwait(false);
        var scriptPath = Path.Combine(scriptDir, "dist", "extract_api.js");

        return await ProcessSandbox.ExecuteWithStreamAsync(
            availability.ExecutablePath!,
            [scriptPath, rootPath, "--json"],
            workingDirectory: scriptDir,
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
            Modules = index.Modules.Select(module => module with
            {
                Classes = module.Classes?.Select(cls =>
                {
                    var typeId = cls.Id ?? BuildTypeId(index.Package, cls.Name);
                    return cls with
                    {
                        Id = typeId,
                        CrossLanguageId = map is not null && map.Ids.TryGetValue(typeId, out var clsXId) ? clsXId : null,
                        Constructors = cls.Constructors?.Select(ctor =>
                        {
                            var ctorId = ctor.Id ?? BuildMemberId(typeId, "constructor");
                            return ctor with
                            {
                                Id = ctorId,
                                CrossLanguageId = map is not null && map.Ids.TryGetValue(ctorId, out var ctorXId) ? ctorXId : null,
                            };
                        }).ToList(),
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
                }).ToList(),
                Interfaces = module.Interfaces?.Select(iface =>
                {
                    var typeId = iface.Id ?? BuildTypeId(index.Package, iface.Name);
                    return iface with
                    {
                        Id = typeId,
                        CrossLanguageId = map is not null && map.Ids.TryGetValue(typeId, out var ifaceXId) ? ifaceXId : null,
                        Methods = iface.Methods?.Select(method =>
                        {
                            var methodId = method.Id ?? BuildMemberId(typeId, method.Name);
                            return method with
                            {
                                Id = methodId,
                                CrossLanguageId = map is not null && map.Ids.TryGetValue(methodId, out var methodXId) ? methodXId : null,
                            };
                        }).ToList(),
                        Properties = iface.Properties?.Select(property =>
                        {
                            var propId = property.Id ?? BuildMemberId(typeId, property.Name);
                            return property with
                            {
                                Id = propId,
                                CrossLanguageId = map is not null && map.Ids.TryGetValue(propId, out var propXId) ? propXId : null,
                            };
                        }).ToList(),
                    };
                }).ToList(),
                Enums = module.Enums?.Select(en =>
                {
                    var enumId = en.Id ?? BuildTypeId(index.Package, en.Name);
                    return en with
                    {
                        Id = enumId,
                        CrossLanguageId = map is not null && map.Ids.TryGetValue(enumId, out var enumXId) ? enumXId : null,
                    };
                }).ToList(),
                Types = module.Types?.Select(type =>
                {
                    var typeId = type.Id ?? BuildTypeId(index.Package, type.Name);
                    return type with
                    {
                        Id = typeId,
                        CrossLanguageId = map is not null && map.Ids.TryGetValue(typeId, out var typeXId) ? typeXId : null,
                    };
                }).ToList(),
                Functions = module.Functions?.Select(function =>
                {
                    var funcId = function.Id ?? BuildTypeId(index.Package, function.Name);
                    return function with
                    {
                        Id = funcId,
                        CrossLanguageId = map is not null && map.Ids.TryGetValue(funcId, out var funcXId) ? funcXId : null,
                    };
                }).ToList(),
            }).ToList(),
        };

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
    /// Extract and format as TypeScript stub syntax.
    /// </summary>
    public async Task<string> ExtractAsTypeScriptAsync(string rootPath, CancellationToken ct = default)
    {
        var result = await RunExtractorAsync("--stub", rootPath, ct).ConfigureAwait(false);
        return result.StandardOutput;
    }

    /// <summary>
    /// Runs the TypeScript extractor with the given output flag, dispatching to the correct
    /// execution mode (NativeBinary, RuntimeInterpreter, or Docker).
    /// </summary>
    private async Task<ProcessResult> RunExtractorAsync(string outputFlag, string rootPath, CancellationToken ct)
    {
        rootPath = ProcessSandbox.ValidateRootPath(rootPath);
        var availability = _availability.GetAvailability();

        if (availability.Mode == ExtractorMode.NativeBinary)
        {
            var result = await ProcessSandbox.ExecuteAsync(
                availability.ExecutablePath!,
                [rootPath, outputFlag],
                cancellationToken: ct
            ).ConfigureAwait(false);

            if (!result.Success)
            {
                var errorMsg = result.TimedOut
                    ? $"TypeScript extractor timed out after {ExtractorTimeout.Value.TotalSeconds}s"
                    : $"TypeScript extractor failed: {result.StandardError}";
                throw new InvalidOperationException(errorMsg);
            }

            return result;
        }

        if (availability.Mode == ExtractorMode.Docker)
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
                    ? $"TypeScript extractor timed out after {ExtractorTimeout.Value.TotalSeconds}s"
                    : $"TypeScript extractor failed: {dockerResult.StandardError}";
                throw new InvalidOperationException(errorMsg);
            }

            return dockerResult;
        }

        if (availability.Mode != ExtractorMode.RuntimeInterpreter)
        {
            throw new InvalidOperationException(availability.UnavailableReason ?? "TypeScript extractor not available");
        }

        // Fall back to Node.js runtime
        var scriptDir = GetScriptDir();
        await EnsureDependenciesAsync(scriptDir, ct).ConfigureAwait(false);

        var scriptPath = Path.Combine(scriptDir, "dist", "extract_api.js");

        var nodeResult = await ProcessSandbox.ExecuteAsync(
            availability.ExecutablePath!,
            [scriptPath, rootPath, outputFlag],
            workingDirectory: scriptDir,
            cancellationToken: ct
        ).ConfigureAwait(false);

        if (!nodeResult.Success)
        {
            var errorMsg = nodeResult.TimedOut
                ? $"TypeScript extractor timed out after {ExtractorTimeout.Value.TotalSeconds}s"
                : $"Node failed: {nodeResult.StandardError}";
            throw new InvalidOperationException(errorMsg);
        }

        return nodeResult;
    }

    internal static async Task EnsureDependenciesAsync(string scriptDir, CancellationToken ct)
    {
        var nodeModules = Path.Combine(scriptDir, "node_modules");

        // Fast path: node_modules already exists (pre-installed during build or previous run)
        if (Directory.Exists(nodeModules)) return;

        // Use semaphore to prevent concurrent npm install on the same directory
        await NpmInstallLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (Directory.Exists(nodeModules)) return;

            // SECURITY: Route through ProcessSandbox for proper timeout, output limits, and argument escaping
            // npm is resolved via PATH - ProcessSandbox will validate the executable
            var result = await ProcessSandbox.ExecuteAsync(
                "npm",
                ["install", "--silent"],
                workingDirectory: scriptDir,
                timeout: TimeSpan.FromMinutes(5), // npm can be slow on cold cache
                cancellationToken: ct
            ).ConfigureAwait(false);

            if (!result.Success)
            {
                var errorMsg = result.TimedOut
                    ? "npm install timed out after 5 minutes"
                    : $"npm install failed: {result.StandardError}";
                throw new InvalidOperationException(errorMsg);
            }
        }
        finally
        {
            NpmInstallLock.Release();
        }
    }

    private static string GetScriptDir()
    {
        // SECURITY: Only load scripts from assembly directory - no path traversal allowed
        var assemblyDir = AppContext.BaseDirectory;
        var distPath = Path.Combine(assemblyDir, "dist", "extract_api.js");

        if (File.Exists(distPath))
            return assemblyDir;

        throw new FileNotFoundException(
            $"Corrupt installation: dist/extract_api.js not found at {distPath}. " +
            "Reinstall the application to resolve this issue.");
    }
}
