// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using ApiExtractor.Contracts;

namespace ApiExtractor.Java;

/// <summary>
/// Extracts public API surface from Java packages using JBang + JavaParser.
/// </summary>
public class JavaApiExtractor : IApiExtractor<ApiIndex>
{
    /// <summary>Shared availability configuration for all Java extractor components.</summary>
    internal static readonly ExtractorConfig SharedConfig = new()
    {
        Language = "java",
        NativeBinaryName = "java_extractor",
        RuntimeToolName = "jbang",
        RuntimeCandidates = ["jbang"]
    };

    private readonly ExtractorAvailabilityProvider _availability = new(SharedConfig);

    /// <inheritdoc />
    public string Language => "java";

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
    public string ToStubs(ApiIndex index) => JavaFormatter.Format(index);

    /// <inheritdoc />
    async Task<ExtractorResult<ApiIndex>> IApiExtractor<ApiIndex>.ExtractAsync(string rootPath, CrossLanguageMap? crossLanguageMap, CancellationToken ct)
    {
        if (!IsAvailable())
            return ExtractorResult<ApiIndex>.CreateFailure(UnavailableReason ?? "JBang not available");

        using var activity = ExtractorTelemetry.StartExtraction(Language, rootPath);
        try
        {
            var (result, diagnostics) = await ExtractCoreAsync(rootPath, crossLanguageMap, ct).ConfigureAwait(false);
            if (result is not null)
            {
                ExtractorTelemetry.RecordResult(activity, true, result.Packages.Count);
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
    /// Extract API from a Java package directory.
    /// Prefers pre-compiled binary from build, falls back to JBang runtime.
    /// </summary>
    public Task<ApiIndex> ExtractAsync(string rootPath, CancellationToken ct = default)
        => ExtractAsync(rootPath, null, ct);

    public async Task<ApiIndex> ExtractAsync(string rootPath, CrossLanguageMap? crossLanguageMap, CancellationToken ct = default)
    {
        var (index, _) = await ExtractCoreAsync(rootPath, crossLanguageMap, ct).ConfigureAwait(false);
        return index ?? throw new InvalidOperationException("Java extraction returned no API surface.");
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
                "Java extractor output was truncated (exceeded output size limit). " +
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
            Packages = index.Packages.Select(package => package with
            {
                Classes = package.Classes?.Select(cls =>
                {
                    var typeId = cls.Id ?? BuildTypeId(package.Name, cls.Name);
                    return cls with
                    {
                        Id = typeId,
                        Constructors = cls.Constructors?.Select(ctor => ctor with { Id = ctor.Id ?? BuildMemberId(typeId, ctor.Name) }).ToList(),
                        Methods = cls.Methods?.Select(method => method with { Id = method.Id ?? BuildMemberId(typeId, method.Name) }).ToList(),
                        Fields = cls.Fields?.Select(field => field with { Id = field.Id ?? BuildMemberId(typeId, field.Name) }).ToList(),
                    };
                }).ToList(),
                Interfaces = package.Interfaces?.Select(iface =>
                {
                    var typeId = iface.Id ?? BuildTypeId(package.Name, iface.Name);
                    return iface with
                    {
                        Id = typeId,
                        Constructors = iface.Constructors?.Select(ctor => ctor with { Id = ctor.Id ?? BuildMemberId(typeId, ctor.Name) }).ToList(),
                        Methods = iface.Methods?.Select(method => method with { Id = method.Id ?? BuildMemberId(typeId, method.Name) }).ToList(),
                        Fields = iface.Fields?.Select(field => field with { Id = field.Id ?? BuildMemberId(typeId, field.Name) }).ToList(),
                    };
                }).ToList(),
                Annotations = package.Annotations?.Select(annotation =>
                {
                    var typeId = annotation.Id ?? BuildTypeId(package.Name, annotation.Name);
                    return annotation with
                    {
                        Id = typeId,
                        Constructors = annotation.Constructors?.Select(ctor => ctor with { Id = ctor.Id ?? BuildMemberId(typeId, ctor.Name) }).ToList(),
                        Methods = annotation.Methods?.Select(method => method with { Id = method.Id ?? BuildMemberId(typeId, method.Name) }).ToList(),
                        Fields = annotation.Fields?.Select(field => field with { Id = field.Id ?? BuildMemberId(typeId, field.Name) }).ToList(),
                    };
                }).ToList(),
                Enums = package.Enums?.Select(en =>
                {
                    var typeId = en.Id ?? BuildTypeId(package.Name, en.Name);
                    return en with
                    {
                        Id = typeId,
                        Methods = en.Methods?.Select(method => method with { Id = method.Id ?? BuildMemberId(typeId, method.Name) }).ToList(),
                    };
                }).ToList(),
            }).ToList(),
        };

    private static ApiIndex ApplyCrossLanguageIds(ApiIndex index, CrossLanguageMap map)
        => index with
        {
            CrossLanguagePackageId = map.PackageId,
            Packages = index.Packages.Select(package => package with
            {
                Classes = package.Classes?.Select(cls => cls with
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
                    Fields = cls.Fields?.Select(field => field with
                    {
                        CrossLanguageId = field.Id is not null && map.Ids.TryGetValue(field.Id, out var fieldCrossLanguageId) ? fieldCrossLanguageId : null,
                    }).ToList(),
                }).ToList(),
                Interfaces = package.Interfaces?.Select(iface => iface with
                {
                    CrossLanguageId = iface.Id is not null && map.Ids.TryGetValue(iface.Id, out var typeCrossLanguageId) ? typeCrossLanguageId : null,
                    Constructors = iface.Constructors?.Select(ctor => ctor with
                    {
                        CrossLanguageId = ctor.Id is not null && map.Ids.TryGetValue(ctor.Id, out var ctorCrossLanguageId) ? ctorCrossLanguageId : null,
                    }).ToList(),
                    Methods = iface.Methods?.Select(method => method with
                    {
                        CrossLanguageId = method.Id is not null && map.Ids.TryGetValue(method.Id, out var methodCrossLanguageId) ? methodCrossLanguageId : null,
                    }).ToList(),
                    Fields = iface.Fields?.Select(field => field with
                    {
                        CrossLanguageId = field.Id is not null && map.Ids.TryGetValue(field.Id, out var fieldCrossLanguageId) ? fieldCrossLanguageId : null,
                    }).ToList(),
                }).ToList(),
                Annotations = package.Annotations?.Select(annotation => annotation with
                {
                    CrossLanguageId = annotation.Id is not null && map.Ids.TryGetValue(annotation.Id, out var typeCrossLanguageId) ? typeCrossLanguageId : null,
                    Constructors = annotation.Constructors?.Select(ctor => ctor with
                    {
                        CrossLanguageId = ctor.Id is not null && map.Ids.TryGetValue(ctor.Id, out var ctorCrossLanguageId) ? ctorCrossLanguageId : null,
                    }).ToList(),
                    Methods = annotation.Methods?.Select(method => method with
                    {
                        CrossLanguageId = method.Id is not null && map.Ids.TryGetValue(method.Id, out var methodCrossLanguageId) ? methodCrossLanguageId : null,
                    }).ToList(),
                    Fields = annotation.Fields?.Select(field => field with
                    {
                        CrossLanguageId = field.Id is not null && map.Ids.TryGetValue(field.Id, out var fieldCrossLanguageId) ? fieldCrossLanguageId : null,
                    }).ToList(),
                }).ToList(),
                Enums = package.Enums?.Select(en => en with
                {
                    CrossLanguageId = en.Id is not null && map.Ids.TryGetValue(en.Id, out var enumCrossLanguageId) ? enumCrossLanguageId : null,
                    Methods = en.Methods?.Select(method => method with
                    {
                        CrossLanguageId = method.Id is not null && map.Ids.TryGetValue(method.Id, out var methodCrossLanguageId) ? methodCrossLanguageId : null,
                    }).ToList(),
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
    /// Extract and format as Java stub syntax.
    /// </summary>
    public async Task<string> ExtractAsJavaAsync(string rootPath, CancellationToken ct = default)
    {
        var result = await RunExtractorAsync("--stub", rootPath, ct).ConfigureAwait(false);
        return result.StandardOutput;
    }

    /// <summary>
    /// Runs the Java extractor with the given output flag, dispatching to the correct
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
                    ? $"Java extractor timed out after {ExtractorTimeout.Value.TotalSeconds}s"
                    : $"Java extractor failed: {result.StandardError}";
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
                    ? $"Java extractor timed out after {ExtractorTimeout.Value.TotalSeconds}s"
                    : $"Java extractor failed: {dockerResult.StandardError}";
                throw new InvalidOperationException(errorMsg);
            }

            return dockerResult;
        }

        if (availability.Mode != ExtractorMode.RuntimeInterpreter)
        {
            throw new InvalidOperationException(availability.UnavailableReason ?? "Java extractor not available");
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
                ? $"Java extractor timed out after {ExtractorTimeout.Value.TotalSeconds}s"
                : $"JBang failed: {jbangResult.StandardError}";
            throw new InvalidOperationException(errorMsg);
        }

        return jbangResult;
    }

    private static string GetScriptPath()
    {
        // SECURITY: Only load scripts from assembly directory - no path traversal allowed
        var assemblyDir = AppContext.BaseDirectory;
        var scriptPath = Path.Combine(assemblyDir, "ExtractApi.java");

        if (File.Exists(scriptPath))
            return scriptPath;

        throw new FileNotFoundException(
            $"Corrupt installation: ExtractApi.java not found at {scriptPath}. " +
            "Reinstall the application to resolve this issue.");
    }
}
