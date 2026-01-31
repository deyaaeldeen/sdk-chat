// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ApiExtractor.Contracts;

/// <summary>
/// Provides secure resolution of external tool paths with support for
/// environment variable overrides and path validation.
/// </summary>
public static class ToolPathResolver
{
    /// <summary>
    /// Environment variable prefix for tool path overrides.
    /// Example: SDK_CHAT_PYTHON_PATH=/usr/local/bin/python3
    /// </summary>
    private const string EnvVarPrefix = "SDK_CHAT_";

    /// <summary>
    /// Resolves the path to an external tool, checking environment overrides first.
    /// </summary>
    /// <param name="toolName">The tool name (e.g., "python", "go", "node")</param>
    /// <param name="defaultCandidates">Default executable names/paths to try</param>
    /// <param name="versionArgs">Arguments to get version (for validation)</param>
    /// <returns>The resolved path, or null if not found</returns>
    public static string? Resolve(string toolName, string[] defaultCandidates, string versionArgs = "--version")
    {
        // 1. Check environment variable override first
        var envVar = $"{EnvVarPrefix}{toolName.ToUpperInvariant()}_PATH";
        var envPath = Environment.GetEnvironmentVariable(envVar);
        
        if (!string.IsNullOrEmpty(envPath))
        {
            if (ValidateExecutable(envPath, versionArgs))
            {
                return envPath;
            }
            // Environment variable set but invalid - warn but continue searching
            Console.Error.WriteLine($"Warning: {envVar}={envPath} is not a valid {toolName} executable");
        }

        // 2. Try default candidates
        foreach (var candidate in defaultCandidates)
        {
            if (ValidateExecutable(candidate, versionArgs))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves the path with detailed result including security warnings.
    /// </summary>
    public static ToolResolutionResult ResolveWithDetails(
        string toolName, 
        string[] defaultCandidates, 
        string versionArgs = "--version")
    {
        var path = Resolve(toolName, defaultCandidates, versionArgs);
        if (path == null)
        {
            return new ToolResolutionResult(null, null, false, $"{toolName} not found");
        }

        var absolutePath = GetAbsolutePath(path);
        var warning = CheckPathSecurity(absolutePath, toolName);
        
        return new ToolResolutionResult(path, absolutePath, true, warning);
    }

    /// <summary>
    /// Validates that an executable exists and runs successfully with given args.
    /// </summary>
    private static bool ValidateExecutable(string path, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return false;
            
            process.WaitForExit(3000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the absolute path to a command using 'which' or 'where'.
    /// </summary>
    private static string? GetAbsolutePath(string command)
    {
        try
        {
            var whichCmd = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "where" : "which";
            var psi = new ProcessStartInfo
            {
                FileName = whichCmd,
                Arguments = command,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return null;
            
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(1000);
            
            return process.ExitCode == 0 ? output.Trim().Split('\n')[0].Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Checks if the tool path is in a trusted location.
    /// </summary>
    private static string? CheckPathSecurity(string? absolutePath, string toolName)
    {
        if (string.IsNullOrEmpty(absolutePath)) return null;

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
            absolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        return isTrusted ? null : $"{toolName} found at non-standard location: {absolutePath}";
    }
}

/// <summary>
/// Result of tool path resolution.
/// </summary>
public sealed record ToolResolutionResult(
    string? Path,
    string? AbsolutePath,
    bool IsAvailable,
    string? WarningOrError
);
