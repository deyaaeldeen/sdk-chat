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
    private string? _unavailableReason;

    /// <inheritdoc />
    public string Language => "typescript";

    /// <inheritdoc />
    public bool IsAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "node",
                Arguments = "--version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
            if (p?.ExitCode == 0) return true;
        }
        catch { }

        _unavailableReason = "Node.js not found. Install Node.js 20+ and ensure it's in PATH.";
        return false;
    }

    /// <inheritdoc />
    public string? UnavailableReason => _unavailableReason;

    /// <inheritdoc />
    public string ToJson(ApiIndex index, bool pretty = false)
        => pretty
            ? JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true })
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
            return ExtractorResult<ApiIndex>.CreateFailure(ex.Message);
        }
    }

    /// <summary>
    /// Extract API from a TypeScript package directory.
    /// </summary>
    public async Task<ApiIndex?> ExtractAsync(string rootPath, CancellationToken ct = default)
    {
        var scriptDir = GetScriptDir();
        await EnsureDependenciesAsync(scriptDir, ct);

        var scriptPath = Path.Combine(scriptDir, "dist", "extract_api.js");
        if (!File.Exists(scriptPath))
        {
            // Fallback to mjs for backwards compatibility
            scriptPath = Path.Combine(scriptDir, "extract_api.mjs");
        }

        var psi = new ProcessStartInfo
        {
            FileName = "node",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = scriptDir
        };
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add(rootPath);
        psi.ArgumentList.Add("--json");

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start node");
        
        // Read streams in parallel to prevent deadlocks when buffer fills
        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(ct));
        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Node failed: {error}");
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        return JsonSerializer.Deserialize<ApiIndex>(output);
    }

    /// <summary>
    /// Extract and format as TypeScript stub syntax.
    /// </summary>
    public async Task<string> ExtractAsTypeScriptAsync(string rootPath, CancellationToken ct = default)
    {
        var scriptDir = GetScriptDir();
        await EnsureDependenciesAsync(scriptDir, ct);

        var scriptPath = Path.Combine(scriptDir, "dist", "extract_api.js");
        if (!File.Exists(scriptPath))
        {
            // Fallback to mjs for backwards compatibility
            scriptPath = Path.Combine(scriptDir, "extract_api.mjs");
        }

        var psi = new ProcessStartInfo
        {
            FileName = "node",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = scriptDir
        };
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add(rootPath);
        psi.ArgumentList.Add("--stub");

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start node");
        
        // Read streams in parallel to prevent deadlocks when buffer fills
        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(ct));
        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Node failed: {error}");
        }

        return output;
    }

    private static async Task EnsureDependenciesAsync(string scriptDir, CancellationToken ct)
    {
        var nodeModules = Path.Combine(scriptDir, "node_modules");
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

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start npm");
        await process.WaitForExitAsync(ct);
    }

    private static string GetScriptDir()
    {
        // Look relative to the executing assembly
        var assemblyDir = Path.GetDirectoryName(typeof(TypeScriptApiExtractor).Assembly.Location) ?? ".";
        var scriptPath = Path.Combine(assemblyDir, "extract_api.mjs");
        
        if (File.Exists(scriptPath))
        {
            return assemblyDir;
        }

        // Dev mode: look in source directory
        var devDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..");
        return Path.GetFullPath(devDir);
    }
}
