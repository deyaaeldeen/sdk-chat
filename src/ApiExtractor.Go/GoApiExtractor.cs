// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text.Json;
using ApiExtractor.Contracts;

namespace ApiExtractor.Go;

/// <summary>
/// Extracts public API surface from Go packages using go/parser.
/// Uses lazy compilation - compiles the extractor binary once and caches it.
/// </summary>
public class GoApiExtractor : IApiExtractor<ApiIndex>
{
    private static readonly SemaphoreSlim CompileLock = new(1, 1);
    private static string? _cachedBinaryPath;

    /// <summary>Shared availability configuration for all Go extractor components.</summary>
    internal static readonly ExtractorConfig SharedConfig = new()
    {
        Language = "go",
        NativeBinaryName = "go_extractor",
        RuntimeToolName = "go",
        RuntimeCandidates = ["go", "/usr/local/go/bin/go", "/opt/go/bin/go"],
        RuntimeValidationArgs = "version"
    };

    private readonly ExtractorAvailabilityProvider _availability = new(SharedConfig);

    /// <inheritdoc />
    public string Language => "go";

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
    public string ToStubs(ApiIndex index) => GoFormatter.Format(index);

    /// <inheritdoc />
    async Task<ExtractorResult<ApiIndex>> IApiExtractor<ApiIndex>.ExtractAsync(string rootPath, CancellationToken ct)
    {
        if (!IsAvailable())
            return ExtractorResult<ApiIndex>.CreateFailure(UnavailableReason ?? "Go not available");

        using var activity = ExtractorTelemetry.StartExtraction(Language, rootPath);
        try
        {
            var (result, warnings) = await ExtractCoreAsync(rootPath, ct).ConfigureAwait(false);
            if (result != null)
            {
                ExtractorTelemetry.RecordResult(activity, true, result.Packages.Count);
                return ExtractorResult<ApiIndex>.CreateSuccess(result, warnings);
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
    /// Extract API from a Go module directory.
    /// Prefers pre-compiled binary from build, falls back to runtime compilation.
    /// </summary>
    public async Task<ApiIndex?> ExtractAsync(string rootPath, CancellationToken ct = default)
    {
        var (index, _) = await ExtractCoreAsync(rootPath, ct).ConfigureAwait(false);
        return index;
    }

    /// <summary>
    /// Shared extraction logic that returns both the API index and any stderr warnings.
    /// </summary>
    private async Task<(ApiIndex? Index, IReadOnlyList<string> Warnings)> ExtractCoreAsync(string rootPath, CancellationToken ct)
    {
        var result = await RunExtractorAsync("--json", rootPath, ct).ConfigureAwait(false);
        var warnings = ParseStderrWarnings(result.StandardError);

        if (string.IsNullOrWhiteSpace(result.StandardOutput))
            return (null, warnings);

        if (result.OutputTruncated)
            throw new InvalidOperationException(
                "Go extractor output was truncated (exceeded output size limit). " +
                "The target package may be too large for extraction.");

        return (JsonSerializer.Deserialize(result.StandardOutput, SourceGenerationContext.Default.ApiIndex), warnings);
    }

    private static IReadOnlyList<string> ParseStderrWarnings(string? stderr)
        => string.IsNullOrWhiteSpace(stderr)
            ? []
            : stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Extract and format as Go stub syntax.
    /// </summary>
    public async Task<string> ExtractAsGoAsync(string rootPath, CancellationToken ct = default)
    {
        var result = await RunExtractorAsync("--stub", rootPath, ct).ConfigureAwait(false);
        return result.StandardOutput;
    }

    /// <summary>
    /// Runs the Go extractor with the given output flag, dispatching to the correct
    /// execution mode (NativeBinary, RuntimeInterpreter, or Docker).
    /// </summary>
    private async Task<ProcessResult> RunExtractorAsync(string outputFlag, string rootPath, CancellationToken ct)
    {
        rootPath = ProcessSandbox.ValidateRootPath(rootPath);
        var availability = _availability.GetAvailability();
        ProcessResult result;

        if (availability.Mode == ExtractorMode.NativeBinary)
        {
            result = await ProcessSandbox.ExecuteAsync(
                availability.ExecutablePath!,
                [outputFlag, rootPath],
                cancellationToken: ct
            ).ConfigureAwait(false);
        }
        else if (availability.Mode == ExtractorMode.RuntimeInterpreter)
        {
            var binaryPath = await EnsureCompiledAsync(availability.ExecutablePath!, ct).ConfigureAwait(false);
            result = await ProcessSandbox.ExecuteAsync(
                binaryPath,
                [outputFlag, rootPath],
                cancellationToken: ct
            ).ConfigureAwait(false);
        }
        else if (availability.Mode == ExtractorMode.Docker)
        {
            result = await DockerSandbox.ExecuteAsync(
                availability.DockerImageName!,
                rootPath,
                [outputFlag, rootPath],
                cancellationToken: ct
            ).ConfigureAwait(false);
        }
        else
        {
            throw new InvalidOperationException(availability.UnavailableReason ?? "Go extractor not available");
        }

        if (!result.Success)
        {
            var errorMsg = result.TimedOut
                ? $"Go extractor timed out after {ExtractorTimeout.Value.TotalSeconds}s"
                : $"Go extractor failed: {result.StandardError}";
            throw new InvalidOperationException(errorMsg);
        }

        return result;
    }

    /// <summary>
    /// Ensures the Go extractor is compiled and cached. Uses content-based hashing
    /// so recompilation only occurs when the source changes.
    /// This is the fallback when pre-compiled binary is not available.
    /// </summary>
    private static async Task<string> EnsureCompiledAsync(string goPath, CancellationToken ct)
    {
        var scriptPath = GetScriptPath();
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException($"extract_api.go not found at {scriptPath}");
        }

        // Fast path: binary already cached in memory
        if (_cachedBinaryPath != null && File.Exists(_cachedBinaryPath))
        {
            return _cachedBinaryPath;
        }

        await CompileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Double-check after acquiring lock
            if (_cachedBinaryPath != null && File.Exists(_cachedBinaryPath))
            {
                return _cachedBinaryPath;
            }

            var sourceContent = await File.ReadAllBytesAsync(scriptPath, ct).ConfigureAwait(false);
            var hash = Convert.ToHexString(SHA256.HashData(sourceContent))[..16].ToLowerInvariant();

            var cacheDir = Path.Combine(Path.GetTempPath(), "sdk-chat", "go-cache");
            Directory.CreateDirectory(cacheDir);

            // Evict stale cached binaries to prevent unbounded disk growth in CI.
            // Only the binary matching the current source hash is kept.
            EvictStaleCacheEntries(cacheDir, hash);

            var binaryName = OperatingSystem.IsWindows() ? $"extractor_{hash}.exe" : $"extractor_{hash}";

            // SECURITY: Validate binary name to prevent path traversal attacks
            // Even though hash is hex-safe, be defensive against future code changes
            ToolPathResolver.ValidateSafeInput(binaryName, nameof(binaryName), allowPath: false);

            var binaryPath = Path.Combine(cacheDir, binaryName);

            if (File.Exists(binaryPath))
            {
                _cachedBinaryPath = binaryPath;
                return binaryPath;
            }

            var compileResult = await ProcessSandbox.ExecuteAsync(
                goPath,
                ["build", "-o", binaryPath, scriptPath],
                timeout: TimeSpan.FromMinutes(3),
                cancellationToken: ct
            ).ConfigureAwait(false);

            if (!compileResult.Success)
            {
                var errorMsg = compileResult.TimedOut
                    ? "go build timed out after 3 minutes"
                    : $"go build failed: {compileResult.StandardError}";
                throw new InvalidOperationException(errorMsg);
            }

            _cachedBinaryPath = binaryPath;
            return binaryPath;
        }
        finally
        {
            CompileLock.Release();
        }
    }

    /// <summary>
    /// Removes cached Go extractor binaries that don't match the current source hash.
    /// Prevents unbounded disk growth in CI environments where the source changes frequently.
    /// </summary>
    private static void EvictStaleCacheEntries(string cacheDir, string currentHash)
    {
        try
        {
            var prefix = "extractor_";
            foreach (var file in Directory.EnumerateFiles(cacheDir, $"{prefix}*"))
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                // Keep the binary matching the current hash
                if (fileName.EndsWith(currentHash, StringComparison.Ordinal))
                    continue;

                try { File.Delete(file); }
                catch { /* Best-effort: file may be in use by another process */ }
            }
        }
        catch
        {
            // Best-effort: cache eviction failure is non-critical
        }
    }

    private static string GetScriptPath()
    {
        // SECURITY: Only load scripts from assembly directory - no path traversal allowed
        var assemblyDir = AppContext.BaseDirectory;
        var scriptPath = Path.Combine(assemblyDir, "extract_api.go");

        if (File.Exists(scriptPath))
            return scriptPath;

        throw new FileNotFoundException(
            $"Corrupt installation: extract_api.go not found at {scriptPath}. " +
            "Reinstall the application to resolve this issue.");
    }
}
