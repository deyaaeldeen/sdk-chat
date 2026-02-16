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
        var result = await RunExtractorAsync("--json", rootPath, ct).ConfigureAwait(false);
        var diagnostics = ParseStderrDiagnostics(result.StandardError);

        if (string.IsNullOrWhiteSpace(result.StandardOutput))
            return (null, diagnostics);

        if (result.OutputTruncated)
            throw new InvalidOperationException(
                "TypeScript extractor output was truncated (exceeded output size limit). " +
                "The target package may be too large for extraction.");

        var index = JsonSerializer.Deserialize(result.StandardOutput, SourceGenerationContext.Default.ApiIndex);
        if (index is null)
        {
            return (null, diagnostics);
        }

        var finalized = FinalizeIndex(index, crossLanguageMap, diagnostics);
        return (finalized, finalized.Diagnostics);
    }

    private static ApiIndex FinalizeIndex(ApiIndex index, CrossLanguageMap? crossLanguageMap, IReadOnlyList<ApiDiagnostic> upstreamDiagnostics)
    {
        var withIds = EnsureIds(index);
        var withCrossLanguage = crossLanguageMap is null ? withIds : ApplyCrossLanguageIds(withIds, crossLanguageMap);
        var diagnostics = ApiDiagnosticsPostProcessor.Build(withCrossLanguage, upstreamDiagnostics);
        return withCrossLanguage with { Diagnostics = diagnostics };
    }

    private static ApiIndex EnsureIds(ApiIndex index)
        => index with
        {
            Modules = index.Modules.Select(module => module with
            {
                Classes = module.Classes?.Select(cls =>
                {
                    var typeId = cls.Id ?? BuildTypeId(index.Package, cls.Name);
                    return cls with
                    {
                        Id = typeId,
                        Constructors = cls.Constructors?.Select(ctor => ctor with
                        {
                            Id = ctor.Id ?? BuildMemberId(typeId, "constructor"),
                        }).ToList(),
                        Methods = cls.Methods?.Select(method => method with
                        {
                            Id = method.Id ?? BuildMemberId(typeId, method.Name),
                        }).ToList(),
                        Properties = cls.Properties?.Select(property => property with
                        {
                            Id = property.Id ?? BuildMemberId(typeId, property.Name),
                        }).ToList(),
                    };
                }).ToList(),
                Interfaces = module.Interfaces?.Select(iface =>
                {
                    var typeId = iface.Id ?? BuildTypeId(index.Package, iface.Name);
                    return iface with
                    {
                        Id = typeId,
                        Methods = iface.Methods?.Select(method => method with
                        {
                            Id = method.Id ?? BuildMemberId(typeId, method.Name),
                        }).ToList(),
                        Properties = iface.Properties?.Select(property => property with
                        {
                            Id = property.Id ?? BuildMemberId(typeId, property.Name),
                        }).ToList(),
                    };
                }).ToList(),
                Enums = module.Enums?.Select(en => en with
                {
                    Id = en.Id ?? BuildTypeId(index.Package, en.Name),
                }).ToList(),
                Types = module.Types?.Select(type => type with
                {
                    Id = type.Id ?? BuildTypeId(index.Package, type.Name),
                }).ToList(),
                Functions = module.Functions?.Select(function => function with
                {
                    Id = function.Id ?? BuildTypeId(index.Package, function.Name),
                }).ToList(),
            }).ToList(),
        };

    private static ApiIndex ApplyCrossLanguageIds(ApiIndex index, CrossLanguageMap map)
        => index with
        {
            CrossLanguagePackageId = map.PackageId,
            Modules = index.Modules.Select(module => module with
            {
                Classes = module.Classes?.Select(cls => cls with
                {
                    CrossLanguageId = cls.Id is not null && map.Ids.TryGetValue(cls.Id, out var typeCrossLanguageId) ? typeCrossLanguageId : null,
                    Constructors = cls.Constructors?.Select(ctor => ctor with
                    {
                        CrossLanguageId = ctor.Id is not null && map.Ids.TryGetValue(ctor.Id, out var ctorCrossLanguageId) ? ctorCrossLanguageId : null,
                    }).ToList(),
                    Methods = cls.Methods?.Select(method => method with
                    {
                        CrossLanguageId = method.Id is not null && map.Ids.TryGetValue(method.Id, out var methodCrossLanguageId) ? methodCrossLanguageId : null,
                    }).ToList(),
                    Properties = cls.Properties?.Select(property => property with
                    {
                        CrossLanguageId = property.Id is not null && map.Ids.TryGetValue(property.Id, out var propertyCrossLanguageId) ? propertyCrossLanguageId : null,
                    }).ToList(),
                }).ToList(),
                Interfaces = module.Interfaces?.Select(iface => iface with
                {
                    CrossLanguageId = iface.Id is not null && map.Ids.TryGetValue(iface.Id, out var typeCrossLanguageId) ? typeCrossLanguageId : null,
                    Methods = iface.Methods?.Select(method => method with
                    {
                        CrossLanguageId = method.Id is not null && map.Ids.TryGetValue(method.Id, out var methodCrossLanguageId) ? methodCrossLanguageId : null,
                    }).ToList(),
                    Properties = iface.Properties?.Select(property => property with
                    {
                        CrossLanguageId = property.Id is not null && map.Ids.TryGetValue(property.Id, out var propertyCrossLanguageId) ? propertyCrossLanguageId : null,
                    }).ToList(),
                }).ToList(),
                Enums = module.Enums?.Select(en => en with
                {
                    CrossLanguageId = en.Id is not null && map.Ids.TryGetValue(en.Id, out var enumCrossLanguageId) ? enumCrossLanguageId : null,
                }).ToList(),
                Types = module.Types?.Select(type => type with
                {
                    CrossLanguageId = type.Id is not null && map.Ids.TryGetValue(type.Id, out var typeCrossLanguageId) ? typeCrossLanguageId : null,
                }).ToList(),
                Functions = module.Functions?.Select(function => function with
                {
                    CrossLanguageId = function.Id is not null && map.Ids.TryGetValue(function.Id, out var functionCrossLanguageId) ? functionCrossLanguageId : null,
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
