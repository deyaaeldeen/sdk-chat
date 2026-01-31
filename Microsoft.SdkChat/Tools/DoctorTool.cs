// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Microsoft.SdkChat.Tools;

/// <summary>
/// Validates all external dependencies required by the SDK Chat tool.
/// Reports version information, path locations, and potential security concerns.
/// </summary>
public sealed partial class DoctorTool
{
    private const string Checkmark = "✓";
    private const string CrossMark = "✗";
    private const string WarningMark = "⚠";
    
    // Source-generated regex for extracting Go version (avoids repeated compilation)
    [GeneratedRegex(@"go(\d+\.\d+\.?\d*)")]
    private static partial Regex GoVersionRegex();

    public record DependencyStatus(
        string Name,
        bool IsAvailable,
        string? Version,
        string? Path,
        string? Warning,
        string? Error
    );

    public async Task<int> ExecuteAsync(bool verbose, CancellationToken ct = default)
    {
        Console.WriteLine("SDK Chat Doctor - Dependency Validation");
        Console.WriteLine("========================================\n");

        var results = new List<DependencyStatus>();

        // Check .NET (always available since we're running on it)
        results.Add(await CheckDotNetAsync(ct));

        // Check Python
        results.Add(await CheckPythonAsync(ct));

        // Check Go
        results.Add(await CheckGoAsync(ct));

        // Check Java/JBang
        results.Add(await CheckJBangAsync(ct));

        // Check Node.js
        results.Add(await CheckNodeAsync(ct));

        // Print results
        PrintResults(results, verbose);

        // Print security advisory
        PrintSecurityAdvisory(results);

        // Return non-zero if any required dependency is missing
        var requiredMissing = results.Any(r => !r.IsAvailable && r.Name == ".NET SDK");
        var optionalMissing = results.Where(r => !r.IsAvailable && r.Name != ".NET SDK").ToList();

        Console.WriteLine();
        if (requiredMissing)
        {
            Console.WriteLine($"{CrossMark} Critical dependencies missing. SDK Chat cannot run.");
            return 1;
        }
        
        if (optionalMissing.Count > 0)
        {
            Console.WriteLine($"{WarningMark} Some language extractors unavailable: {string.Join(", ", optionalMissing.Select(m => m.Name))}");
            Console.WriteLine("  SDK Chat will work but cannot extract APIs for these languages.");
            return 0;
        }

        Console.WriteLine($"{Checkmark} All dependencies available. SDK Chat is fully operational.");
        return 0;
    }

    private static async Task<DependencyStatus> CheckDotNetAsync(CancellationToken ct)
    {
        try
        {
            var (exitCode, output, _) = await RunCommandAsync("dotnet", "--version", ct);
            if (exitCode == 0)
            {
                var path = await GetCommandPathAsync("dotnet", ct);
                return new DependencyStatus(
                    ".NET SDK",
                    true,
                    output.Trim(),
                    path,
                    null,
                    null
                );
            }
        }
        catch { }

        return new DependencyStatus(".NET SDK", false, null, null, null, "dotnet not found in PATH");
    }

    private static async Task<DependencyStatus> CheckPythonAsync(CancellationToken ct)
    {
        foreach (var cmd in new[] { "python3", "python" })
        {
            try
            {
                var (exitCode, output, _) = await RunCommandAsync(cmd, "--version", ct);
                if (exitCode == 0)
                {
                    var version = output.Trim().Replace("Python ", "");
                    var path = await GetCommandPathAsync(cmd, ct);

                    // Check for minimum version (3.9+)
                    string? warning = null;
                    if (Version.TryParse(version.Split('-')[0], out var ver) && ver < new Version(3, 9))
                    {
                        warning = "Python 3.9+ recommended for best compatibility";
                    }

                    // Security: Check if path is in a trusted location
                    var pathWarning = CheckPathSecurity(path, "python");
                    if (pathWarning != null) warning = pathWarning;

                    return new DependencyStatus("Python", true, version, path, warning, null);
                }
            }
            catch { }
        }

        return new DependencyStatus("Python", false, null, null, null, 
            "Python 3 not found. Install Python 3.9+ and ensure it's in PATH.");
    }

    private static async Task<DependencyStatus> CheckGoAsync(CancellationToken ct)
    {
        try
        {
            var (exitCode, output, _) = await RunCommandAsync("go", "version", ct);
            if (exitCode == 0)
            {
                // "go version go1.21.0 darwin/arm64" -> "1.21.0"
                var version = output.Trim();
                var match = GoVersionRegex().Match(version);
                var versionStr = match.Success ? match.Groups[1].Value : version;
                var path = await GetCommandPathAsync("go", ct);

                var pathWarning = CheckPathSecurity(path, "go");

                return new DependencyStatus("Go", true, versionStr, path, pathWarning, null);
            }
        }
        catch { }

        return new DependencyStatus("Go", false, null, null, null,
            "Go not found. Install Go (https://go.dev) and ensure it's in PATH.");
    }

    private static async Task<DependencyStatus> CheckJBangAsync(CancellationToken ct)
    {
        try
        {
            var (exitCode, output, _) = await RunCommandAsync("jbang", "--version", ct);
            if (exitCode == 0)
            {
                var path = await GetCommandPathAsync("jbang", ct);
                var pathWarning = CheckPathSecurity(path, "jbang");

                return new DependencyStatus("JBang (Java)", true, output.Trim(), path, pathWarning, null);
            }
        }
        catch { }

        return new DependencyStatus("JBang (Java)", false, null, null, null,
            "JBang not found. Install JBang (https://jbang.dev) and ensure it's in PATH.");
    }

    private static async Task<DependencyStatus> CheckNodeAsync(CancellationToken ct)
    {
        try
        {
            var (exitCode, output, _) = await RunCommandAsync("node", "--version", ct);
            if (exitCode == 0)
            {
                var version = output.Trim().TrimStart('v');
                var path = await GetCommandPathAsync("node", ct);

                // Check for minimum version (18+)
                string? warning = null;
                if (Version.TryParse(version.Split('-')[0], out var ver) && ver.Major < 18)
                {
                    warning = "Node.js 18+ recommended for best compatibility";
                }

                var pathWarning = CheckPathSecurity(path, "node");
                if (pathWarning != null) warning = pathWarning;

                return new DependencyStatus("Node.js", true, version, path, warning, null);
            }
        }
        catch { }

        return new DependencyStatus("Node.js", false, null, null, null,
            "Node.js not found. Install Node.js 18+ and ensure it's in PATH.");
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunCommandAsync(
        string command, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {command}");

        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);
        
        return (process.ExitCode, await outputTask, await errorTask);
    }

    private static async Task<string?> GetCommandPathAsync(string command, CancellationToken ct)
    {
        try
        {
            var whichCmd = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "where" : "which";
            var (exitCode, output, _) = await RunCommandAsync(whichCmd, command, ct);
            if (exitCode == 0)
            {
                return output.Trim().Split('\n')[0].Trim();
            }
        }
        catch { }
        return null;
    }

    private static string? CheckPathSecurity(string? path, string toolName)
    {
        if (string.IsNullOrEmpty(path)) return null;

        // Normalize path
        var normalizedPath = Path.GetFullPath(path);

        // Define trusted locations based on OS
        var trustedPrefixes = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { 
                @"C:\Program Files", 
                @"C:\Program Files (x86)", 
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"C:\Windows"
              }
            : new[] { 
                "/usr/bin", 
                "/usr/local/bin", 
                "/opt", 
                "/home", 
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
              };

        var isTrusted = trustedPrefixes.Any(prefix => 
            normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        if (!isTrusted)
        {
            return $"Security: {toolName} found at non-standard location. Verify authenticity.";
        }

        return null;
    }

    private static void PrintResults(List<DependencyStatus> results, bool verbose)
    {
        foreach (var dep in results)
        {
            var icon = dep.IsAvailable ? Checkmark : CrossMark;
            var status = dep.IsAvailable ? $"v{dep.Version}" : "NOT FOUND";
            
            Console.WriteLine($"{icon} {dep.Name,-15} {status}");
            
            if (verbose && dep.IsAvailable && dep.Path != null)
            {
                Console.WriteLine($"  Path: {dep.Path}");
            }
            
            if (dep.Warning != null)
            {
                Console.WriteLine($"  {WarningMark} {dep.Warning}");
            }
            
            if (!dep.IsAvailable && dep.Error != null)
            {
                Console.WriteLine($"  {dep.Error}");
            }
        }
    }

    private static void PrintSecurityAdvisory(List<DependencyStatus> results)
    {
        var warnings = results.Where(r => r.Warning != null && r.Warning.StartsWith("Security:")).ToList();
        if (warnings.Count == 0) return;

        Console.WriteLine();
        Console.WriteLine("Security Advisory");
        Console.WriteLine("-----------------");
        Console.WriteLine("The following tools were found in non-standard locations.");
        Console.WriteLine("In shared or containerized environments, verify these are authentic:");
        foreach (var w in warnings)
        {
            Console.WriteLine($"  • {w.Name}: {w.Path}");
        }
        Console.WriteLine();
        Console.WriteLine("To enforce specific paths, set environment variables:");
        Console.WriteLine("  SDK_CHAT_PYTHON_PATH, SDK_CHAT_GO_PATH, SDK_CHAT_NODE_PATH, SDK_CHAT_JBANG_PATH");
    }
}
