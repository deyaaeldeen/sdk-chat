// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text.Json;
using ApiExtractor.Contracts;

namespace ApiExtractor.Java;

/// <summary>
/// Extracts public API surface from Java packages using JBang + JavaParser.
/// </summary>
public class JavaApiExtractor : IApiExtractor<ApiIndex>
{
    private string? _unavailableReason;

    /// <inheritdoc />
    public string Language => "java";

    /// <inheritdoc />
    public bool IsAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "jbang",
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
        
        _unavailableReason = "JBang not found. Install JBang (https://jbang.dev) and ensure it's in PATH.";
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
    public string ToStubs(ApiIndex index) => JavaFormatter.Format(index);

    /// <inheritdoc />
    async Task<ExtractorResult<ApiIndex>> IApiExtractor<ApiIndex>.ExtractAsync(string rootPath, CancellationToken ct)
    {
        if (!IsAvailable())
            return ExtractorResult<ApiIndex>.CreateFailure(UnavailableReason ?? "JBang not available");

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
    /// Extract API from a Java package directory.
    /// </summary>
    public async Task<ApiIndex?> ExtractAsync(string rootPath, CancellationToken ct = default)
    {
        var scriptPath = GetScriptPath();
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException($"ExtractApi.java not found at {scriptPath}");
        }

        var psi = new ProcessStartInfo
        {
            FileName = "jbang",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add(rootPath);
        psi.ArgumentList.Add("--json");

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start jbang");
        
        // Read streams in parallel to prevent deadlocks when buffer fills
        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(ct));
        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"jbang failed: {error}");
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        return JsonSerializer.Deserialize<ApiIndex>(output);
    }

    /// <summary>
    /// Extract and format as Java stub syntax.
    /// </summary>
    public async Task<string> ExtractAsJavaAsync(string rootPath, CancellationToken ct = default)
    {
        var scriptPath = GetScriptPath();
        
        var psi = new ProcessStartInfo
        {
            FileName = "jbang",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add(rootPath);
        psi.ArgumentList.Add("--stub");

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start jbang");
        
        // Read streams in parallel to prevent deadlocks when buffer fills
        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(ct));
        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"jbang failed: {error}");
        }

        return output;
    }

    private static string GetScriptPath()
    {
        // Look relative to the executing assembly
        var assemblyDir = Path.GetDirectoryName(typeof(JavaApiExtractor).Assembly.Location) ?? ".";
        var scriptPath = Path.Combine(assemblyDir, "ExtractApi.java");
        
        if (!File.Exists(scriptPath))
        {
            // Dev mode: look in source directory
            scriptPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "ExtractApi.java");
        }
        
        return Path.GetFullPath(scriptPath);
    }
}
