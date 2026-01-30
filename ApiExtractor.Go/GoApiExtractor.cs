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
        var result = ToolPathResolver.ResolveWithDetails("go", GoPaths, "version");
        if (!result.IsAvailable)
        {
            _unavailableReason = result.WarningOrError ?? "Go not found. Install Go (https://go.dev) and ensure it's in PATH.";
            return false;
        }
        _goPath = result.Path;
        if (result.WarningOrError != null)
        {
            Console.Error.WriteLine($"Warning: {result.WarningOrError}");
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
            return ExtractorResult<ApiIndex>.CreateFailure($"{ex.Message}\n{ex.StackTrace}");
        }
    }
    
    /// <summary>
    /// Extract API from a Go module directory.
    /// </summary>
    public async Task<ApiIndex?> ExtractAsync(string rootPath, CancellationToken ct = default)
    {
        var goPath = _goPath ?? ToolPathResolver.Resolve("go", GoPaths, "version") 
            ?? throw new FileNotFoundException("Go executable not found");
        
        var scriptPath = GetScriptPath();
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException($"extract_api.go not found at {scriptPath}");
        }

        var psi = new ProcessStartInfo
        {
            FileName = goPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add("--json");
        psi.ArgumentList.Add(rootPath);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start go");
        
        // Read streams in parallel to prevent deadlocks when buffer fills
        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(ct));
        var output = await outputTask;
        var error = await errorTask;

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
        var goPath = _goPath ?? ToolPathResolver.Resolve("go", GoPaths, "version") 
            ?? throw new FileNotFoundException("Go executable not found");
        var scriptPath = GetScriptPath();
        
        var psi = new ProcessStartInfo
        {
            FileName = goPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add("--stub");
        psi.ArgumentList.Add(rootPath);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start go");
        
        // Read streams in parallel to prevent deadlocks when buffer fills
        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(ct));
        var output = await outputTask;
        var error = await errorTask;

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
