// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text.Json;
using ApiExtractor.Contracts;

namespace ApiExtractor.TypeScript;

/// <summary>
/// Extracts public API surface from TypeScript packages using ts-morph.
/// </summary>
public class TypeScriptApiExtractor : IApiExtractor<ApiIndex>
{
    private static readonly string[] NodeCandidates = { "node" };
    private static readonly SemaphoreSlim NpmInstallLock = new(1, 1);

    private string? _nodePath;
    private string? _unavailableReason;
    private string? _warning;

    /// <inheritdoc />
    public string Language => "typescript";

    /// <summary>
    /// Warning message from tool resolution (if any).
    /// </summary>
    public string? Warning => _warning;

    /// <inheritdoc />
    public bool IsAvailable()
    {
        var result = ToolPathResolver.ResolveWithDetails("node", NodeCandidates);
        if (!result.IsAvailable)
        {
            _unavailableReason = "Node.js not found. Install Node.js 20+ and ensure it's in PATH.";
            return false;
        }
        _nodePath = result.Path;
        _warning = result.WarningOrError; // Store warning for structured logging by caller
        return true;
    }

    /// <inheritdoc />
    public string? UnavailableReason => _unavailableReason;

    /// <inheritdoc />
    public string ToJson(ApiIndex index, bool pretty = false)
        => pretty
            ? JsonSerializer.Serialize(index, JsonOptionsCache.Indented)
            : JsonSerializer.Serialize(index);

    /// <inheritdoc />
    public string ToStubs(ApiIndex index) => TypeScriptFormatter.Format(index);

    /// <inheritdoc />
    async Task<ExtractorResult<ApiIndex>> IApiExtractor<ApiIndex>.ExtractAsync(string rootPath, CancellationToken ct)
    {
        if (!IsAvailable())
            return ExtractorResult<ApiIndex>.CreateFailure(UnavailableReason ?? "Node.js not available");

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
    /// Extract API from a TypeScript package directory.
    /// </summary>
    public async Task<ApiIndex?> ExtractAsync(string rootPath, CancellationToken ct = default)
    {
        var nodePath = _nodePath ?? ToolPathResolver.Resolve("node", NodeCandidates)
            ?? throw new InvalidOperationException("Node.js not found");

        var scriptDir = GetScriptDir();
        await EnsureDependenciesAsync(scriptDir, ct).ConfigureAwait(false);

        var scriptPath = Path.Combine(scriptDir, "dist", "extract_api.js");
        if (!File.Exists(scriptPath))
        {
            // Fallback to mjs for backwards compatibility
            scriptPath = Path.Combine(scriptDir, "extract_api.mjs");
        }

        // Use ProcessSandbox for hardened execution with timeout and output limits
        var result = await ProcessSandbox.ExecuteAsync(
            nodePath,
            [scriptPath, rootPath, "--json"],
            workingDirectory: scriptDir,
            cancellationToken: ct
        ).ConfigureAwait(false);

        if (!result.Success)
        {
            var errorMsg = result.TimedOut
                ? $"TypeScript extractor timed out after {ExtractorTimeout.Value.TotalSeconds}s"
                : $"Node failed: {result.StandardError}";
            throw new InvalidOperationException(errorMsg);
        }

        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return null;
        }

        return JsonSerializer.Deserialize<ApiIndex>(result.StandardOutput);
    }

    /// <summary>
    /// Extract and format as TypeScript stub syntax.
    /// </summary>
    public async Task<string> ExtractAsTypeScriptAsync(string rootPath, CancellationToken ct = default)
    {
        var nodePath = _nodePath ?? ToolPathResolver.Resolve("node", NodeCandidates)
            ?? throw new InvalidOperationException("Node.js not found");

        var scriptDir = GetScriptDir();
        await EnsureDependenciesAsync(scriptDir, ct).ConfigureAwait(false);

        var scriptPath = Path.Combine(scriptDir, "dist", "extract_api.js");
        if (!File.Exists(scriptPath))
        {
            // Fallback to mjs for backwards compatibility
            scriptPath = Path.Combine(scriptDir, "extract_api.mjs");
        }

        // Use ProcessSandbox for hardened execution with timeout and output limits
        var result = await ProcessSandbox.ExecuteAsync(
            nodePath,
            [scriptPath, rootPath, "--stub"],
            workingDirectory: scriptDir,
            cancellationToken: ct
        ).ConfigureAwait(false);

        if (!result.Success)
        {
            var errorMsg = result.TimedOut
                ? $"TypeScript extractor timed out after {ExtractorTimeout.Value.TotalSeconds}s"
                : $"Node failed: {result.StandardError}";
            throw new InvalidOperationException(errorMsg);
        }

        return result.StandardOutput;
    }

    private static async Task EnsureDependenciesAsync(string scriptDir, CancellationToken ct)
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

            var psi = new ProcessStartInfo
            {
                FileName = "npm",
                Arguments = "install --silent",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = scriptDir
            };

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start npm. Ensure Node.js is installed.");
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync(ct);
                throw new InvalidOperationException($"npm install failed: {error}");
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
        var scriptPath = Path.Combine(assemblyDir, "extract_api.mjs");

        if (File.Exists(scriptPath))
            return assemblyDir;

        // Also check for compiled dist version
        var distPath = Path.Combine(assemblyDir, "dist", "extract_api.js");
        if (File.Exists(distPath))
            return assemblyDir;

        throw new FileNotFoundException(
            $"Corrupt installation: extract_api.mjs not found at {scriptPath}. " +
            "Reinstall the application to resolve this issue.");
    }
}
