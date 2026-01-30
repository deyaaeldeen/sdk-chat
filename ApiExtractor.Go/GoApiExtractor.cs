using System.Diagnostics;
using System.Text.Json;
using ApiExtractor.Contracts;

namespace ApiExtractor.Go;

/// <summary>
/// Extracts public API surface from Go packages using go/parser.
/// </summary>
public class GoApiExtractor : IApiExtractor<ApiIndex>
{
    private static readonly string[] GoPaths = { "go", "/usr/local/go/bin/go", "/opt/go/bin/go" };
    
    private string? _goPath;
    private string? _unavailableReason;

    /// <inheritdoc />
    public string Language => "go";

    /// <inheritdoc />
    public bool IsAvailable()
    {
        _goPath = FindGoExecutable();
        if (_goPath == null)
        {
            _unavailableReason = "Go not found. Install Go (https://go.dev) and ensure it's in PATH.";
            return false;
        }
        return true;
    }

    /// <inheritdoc />
    public string? UnavailableReason => _unavailableReason;

    /// <inheritdoc />
    public string ToJson(ApiIndex index, bool pretty = false)
        => pretty
            ? JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true })
            : JsonSerializer.Serialize(index);

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
            return ExtractorResult<ApiIndex>.CreateFailure(ex.Message);
        }
    }
    
    private static string? FindGoExecutable()
    {
        foreach (var path in GoPaths)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = "version",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(1000);
                if (p?.ExitCode == 0) return path;
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// Extract API from a Go module directory.
    /// </summary>
    public async Task<ApiIndex?> ExtractAsync(string rootPath, CancellationToken ct = default)
    {
        var goPath = _goPath ?? FindGoExecutable() ?? throw new FileNotFoundException("Go executable not found");
        
        var scriptPath = GetScriptPath();
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException($"extract_api.go not found at {scriptPath}");
        }

        var psi = new ProcessStartInfo
        {
            FileName = goPath,
            Arguments = $"run \"{scriptPath}\" --json \"{rootPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start go");
        
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        var error = await process.StandardError.ReadToEndAsync(ct);
        
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"go run failed: {error}");
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        return JsonSerializer.Deserialize<ApiIndex>(output);
    }

    /// <summary>
    /// Extract and format as Go stub syntax.
    /// </summary>
    public async Task<string> ExtractAsGoAsync(string rootPath, CancellationToken ct = default)
    {
        var goPath = _goPath ?? FindGoExecutable() ?? throw new FileNotFoundException("Go executable not found");
        var scriptPath = GetScriptPath();
        
        var psi = new ProcessStartInfo
        {
            FileName = goPath,
            Arguments = $"run \"{scriptPath}\" --stub \"{rootPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start go");
        
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        var error = await process.StandardError.ReadToEndAsync(ct);
        
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"go run failed: {error}");
        }

        return output;
    }

    private static string GetScriptPath()
    {
        // Look relative to the executing assembly
        var assemblyDir = Path.GetDirectoryName(typeof(GoApiExtractor).Assembly.Location) ?? ".";
        var scriptPath = Path.Combine(assemblyDir, "extract_api.go");
        
        if (!File.Exists(scriptPath))
        {
            // Dev mode: look in source directory
            scriptPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "extract_api.go");
        }
        
        return Path.GetFullPath(scriptPath);
    }
}
