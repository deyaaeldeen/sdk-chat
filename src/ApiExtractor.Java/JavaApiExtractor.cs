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
