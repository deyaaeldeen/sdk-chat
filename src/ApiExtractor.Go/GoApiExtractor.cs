// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
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
    private static readonly string[] GoPaths = { "go", "/usr/local/go/bin/go", "/opt/go/bin/go" };
    private static readonly SemaphoreSlim CompileLock = new(1, 1);
    private static string? _cachedBinaryPath;

    private string? _goPath;
    private string? _unavailableReason;
    private string? _warning;

    /// <inheritdoc />
    public string Language => "go";

    /// <summary>
    /// Warning message from tool resolution (if any).
    /// </summary>
    public string? Warning => _warning;

    /// <inheritdoc />
    public bool IsAvailable()
    {
        // Check for precompiled binary first (used in release container)
        if (GetPrecompiledBinaryPath() != null)
        {
            return true;
        }

        // Fall back to Go runtime for compilation
        var result = ToolPathResolver.ResolveWithDetails("go", GoPaths, "version");
        if (!result.IsAvailable)
        {
            _unavailableReason = result.WarningOrError ?? "Go not found. Install Go (https://go.dev) and ensure it's in PATH.";
            return false;
        }
        _goPath = result.Path;
        _warning = result.WarningOrError; // Store warning for structured logging by caller
        return true;
    }

    /// <inheritdoc />
    public string? UnavailableReason => _unavailableReason;

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

        try
        {
            var result = await ExtractAsync(rootPath, ct).ConfigureAwait(false);
            return result != null
                ? ExtractorResult<ApiIndex>.CreateSuccess(result)
                : ExtractorResult<ApiIndex>.CreateFailure("No API surface extracted");
        }
        catch (Exception ex)
        {
            return ExtractorResult<ApiIndex>.CreateFailure($"{ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>
    /// Extract API from a Go module directory.
    /// Prefers pre-compiled binary from build, falls back to runtime compilation.
    /// </summary>
    public async Task<ApiIndex?> ExtractAsync(string rootPath, CancellationToken ct = default)
    {
        var binaryPath = GetPrecompiledBinaryPath();

        if (binaryPath == null)
        {
            // Fall back to runtime compilation if pre-compiled binary not available
            var goPath = _goPath ?? ToolPathResolver.Resolve("go", GoPaths, "version")
                ?? throw new FileNotFoundException("Go executable not found. Pre-compiled binary also not available.");

            binaryPath = await EnsureCompiledAsync(goPath, ct).ConfigureAwait(false);
        }

        // Use ProcessSandbox for hardened execution with timeout and output limits
        var result = await ProcessSandbox.ExecuteAsync(
            binaryPath,
            ["--json", rootPath],
            cancellationToken: ct
        ).ConfigureAwait(false);

        if (!result.Success)
        {
            var errorMsg = result.TimedOut
                ? $"Go extractor timed out after {ExtractorTimeout.Value.TotalSeconds}s"
                : $"Extractor failed: {result.StandardError}";
            throw new InvalidOperationException(errorMsg);
        }

        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return null;
        }

        return JsonSerializer.Deserialize(result.StandardOutput, SourceGenerationContext.Default.ApiIndex);
    }

    /// <summary>
    /// Extract and format as Go stub syntax.
    /// </summary>
    public async Task<string> ExtractAsGoAsync(string rootPath, CancellationToken ct = default)
    {
        var binaryPath = GetPrecompiledBinaryPath();

        if (binaryPath == null)
        {
            // Fall back to runtime compilation if pre-compiled binary not available
            var goPath = _goPath ?? ToolPathResolver.Resolve("go", GoPaths, "version")
                ?? throw new FileNotFoundException("Go executable not found. Pre-compiled binary also not available.");

            binaryPath = await EnsureCompiledAsync(goPath, ct).ConfigureAwait(false);
        }

        // Use ProcessSandbox for hardened execution with timeout and output limits
        var result = await ProcessSandbox.ExecuteAsync(
            binaryPath,
            ["--stub", rootPath],
            cancellationToken: ct
        ).ConfigureAwait(false);

        if (!result.Success)
        {
            var errorMsg = result.TimedOut
                ? $"Go extractor timed out after {ExtractorTimeout.Value.TotalSeconds}s"
                : $"Extractor failed: {result.StandardError}";
            throw new InvalidOperationException(errorMsg);
        }

        return result.StandardOutput;
    }

    /// <summary>
    /// Gets the path to the pre-compiled Go extractor binary if it exists.
    /// The binary is compiled during build and placed in the assembly directory.
    /// </summary>
    /// <returns>Path to pre-compiled binary, or null if not available.</returns>
    private static string? GetPrecompiledBinaryPath()
    {
        var assemblyDir = AppContext.BaseDirectory;
        var binaryName = OperatingSystem.IsWindows() ? "go_extractor.exe" : "go_extractor";
        var binaryPath = Path.Combine(assemblyDir, binaryName);

        if (File.Exists(binaryPath))
        {
            _cachedBinaryPath = binaryPath;
            return binaryPath;
        }

        return null;
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

            // Compute hash of source for cache key
            var sourceContent = await File.ReadAllBytesAsync(scriptPath, ct).ConfigureAwait(false);
            var hash = Convert.ToHexString(SHA256.HashData(sourceContent))[..16].ToLowerInvariant();

            // Cache in user's temp directory
            var cacheDir = Path.Combine(Path.GetTempPath(), "sdk-chat", "go-cache");
            Directory.CreateDirectory(cacheDir);

            var binaryName = OperatingSystem.IsWindows() ? $"extractor_{hash}.exe" : $"extractor_{hash}";
            var binaryPath = Path.Combine(cacheDir, binaryName);

            // Check if binary exists and is valid
            if (File.Exists(binaryPath))
            {
                _cachedBinaryPath = binaryPath;
                return binaryPath;
            }

            // Compile the binary
            var psi = new ProcessStartInfo
            {
                FileName = goPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("build");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(binaryPath);
            psi.ArgumentList.Add(scriptPath);

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start go build");

            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            var errorTask = process.StandardError.ReadToEndAsync(ct);
            await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(ct)).ConfigureAwait(false);
            var error = await errorTask;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"go build failed: {error}");
            }

            _cachedBinaryPath = binaryPath;
            return binaryPath;
        }
        finally
        {
            CompileLock.Release();
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
